using System.Text;
using FlashTrans.Interop;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace FlashTrans.Services;

/// <summary>
/// 文字识别，走系统自带的 Windows.Media.Ocr——不需要联网，也不用额外装东西。
/// 能识别哪些语言取决于系统装了哪些「语言 → 可选功能 → 光学字符识别」包。
/// </summary>
public static class OcrService
{
    static readonly object Gate = new();
    static readonly Dictionary<string, OcrEngine?> Engines = new(StringComparer.OrdinalIgnoreCase);
    static string[]? _available;

    /// <summary>系统装了 OCR 包的语言标签（BCP-47），按系统给的顺序。</summary>
    public static string[] AvailableLanguages
    {
        get
        {
            if (_available is not null) return _available;
            lock (Gate)
            {
                if (_available is not null) return _available;
                try
                {
                    _available = OcrEngine.AvailableRecognizerLanguages
                        .Select(l => l.LanguageTag).ToArray();
                }
                catch (Exception ex)
                {
                    Log.Warn("枚举 OCR 语言失败：" + ex.Message);
                    _available = [];
                }
                return _available;
            }
        }
    }

    public static bool IsAvailable => AvailableLanguages.Length > 0;

    /// <summary>
    /// 挑一个能用的识别语言。传进来的是本程序的统一语言代码（zh-CN / en / ja …）。
    /// 系统里可能装的是 zh-Hans-CN 这类更长的标签，所以按前缀匹配。
    /// </summary>
    public static string? ResolveLanguage(string? preferred)
    {
        var langs = AvailableLanguages;
        if (langs.Length == 0) return null;

        foreach (var want in Candidates(preferred))
        {
            var hit = langs.FirstOrDefault(l => l.Equals(want, StringComparison.OrdinalIgnoreCase))
                   ?? langs.FirstOrDefault(l => l.StartsWith(want + "-", StringComparison.OrdinalIgnoreCase))
                   ?? langs.FirstOrDefault(l => want.StartsWith(l + "-", StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return langs[0];
    }

    static IEnumerable<string> Candidates(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && preferred != Core.Languages.Auto)
        {
            yield return preferred!;
            // zh-CN 在系统里叫 zh-Hans-CN，zh-TW 叫 zh-Hant-TW
            if (preferred.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) yield return "zh-Hans";
            if (preferred.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)) yield return "zh-Hant";
            var dash = preferred!.IndexOf('-');
            if (dash > 0) yield return preferred[..dash];
        }
        // 兜底顺序：英文最通用，中文包在国内机器上几乎都有
        yield return "en";
        yield return "zh-Hans";
    }

    /// <summary>识别一块像素里的文字。识别不出来返回空串。</summary>
    public static async Task<string> RecognizeAsync(CapturedImage image, string? preferred,
                                                    CancellationToken ct = default)
    {
        var tag = ResolveLanguage(preferred);
        if (tag is null) throw new InvalidOperationException(NoEngineHint());

        var engine = EngineFor(tag) ?? throw new InvalidOperationException(NoEngineHint());

        // OcrEngine 对小图很吃亏，放大一倍能明显多认出几个字
        var img = image.ScaleUpTo(360);
        if (img.Width > OcrEngine.MaxImageDimension || img.Height > OcrEngine.MaxImageDimension)
            throw new InvalidOperationException(
                $"截取的区域太大（上限 {OcrEngine.MaxImageDimension} 像素），选小一点");

        using var bitmap = ToSoftwareBitmap(img);
        ct.ThrowIfCancellationRequested();

        var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
        return Compose(result, tag);
    }

    /// <summary>
    /// 像素搬进 SoftwareBitmap。走 DataWriter → IBuffer 这条投影出来的路：
    /// 常见的 LockBuffer + IMemoryBufferByteAccess 写法在 .NET 5+ 上会抛
    /// InvalidCastException——CsWinRT 的对象不是真正的 COM RCW，ComImport 接口转不过去。
    /// 多拷一遍内存，但截图这点大小无所谓。
    /// </summary>
    static SoftwareBitmap ToSoftwareBitmap(CapturedImage img)
    {
        using var writer = new Windows.Storage.Streams.DataWriter();
        writer.WriteBytes(img.Pixels);
        var buffer = writer.DetachBuffer();
        return SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8,
                                                   img.Width, img.Height,
                                                   BitmapAlphaMode.Premultiplied);
    }

    /// <summary>
    /// 拼行。行间距明显变大的地方当成换段，读起来更接近原排版。
    /// </summary>
    static string Compose(OcrResult result, string tag)
    {
        var lines = result.Lines;
        if (lines.Count == 0) return "";

        var cjk = tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
               || tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

        var texts = new List<string>(lines.Count);
        var tops = new List<double>(lines.Count);
        var heights = new List<double>(lines.Count);
        foreach (var line in lines)
        {
            var t = LineText(line, cjk);
            if (string.IsNullOrEmpty(t)) continue;
            texts.Add(t);
            var rects = line.Words.Select(w => w.BoundingRect).ToList();
            tops.Add(rects.Count > 0 ? rects.Min(r => r.Top) : 0);
            heights.Add(rects.Count > 0 ? rects.Max(r => r.Height) : 0);
        }
        if (texts.Count == 0) return "";

        var typical = heights.Where(h => h > 0).DefaultIfEmpty(0).Average();
        var sb = new StringBuilder(texts[0]);
        for (var i = 1; i < texts.Count; i++)
        {
            var gap = tops[i] - tops[i - 1];
            // 行距超过一行半，基本是换段或另一块文字
            var newParagraph = typical > 0 && gap > typical * 1.8;
            if (newParagraph) sb.Append('\n');
            else if (cjk) { /* 中日文断行处不该多出空格 */ }
            else if (!EndsHyphenated(sb)) sb.Append(' ');
            else sb.Length--;   // 行尾连字符：去掉它把单词接回去
            sb.Append(texts[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 一行里的字怎么连起来。OcrLine.Text 是把「词」用空格拼的，中文每个字往往
    /// 各算一个词，直接用就成了「闪 译 一 下」，送去翻译质量明显变差。
    /// 所以中日文按词边界自己拼：两边只要有一个是汉字/假名就不加空格，
    /// 中间夹的西文（OCR 1234）仍然保留空格。
    /// </summary>
    static string LineText(OcrLine line, bool cjk)
    {
        if (!cjk) return line.Text?.Trim() ?? "";

        var sb = new StringBuilder();
        foreach (var word in line.Words)
        {
            var t = word.Text;
            if (string.IsNullOrEmpty(t)) continue;
            if (sb.Length > 0 && !IsCjk(sb[^1]) && !IsCjk(t[0])) sb.Append(' ');
            sb.Append(t);
        }
        // Words 为空的行（少见）退回引擎给的整行文本
        return sb.Length > 0 ? sb.ToString().Trim() : line.Text?.Trim() ?? "";
    }

    static bool IsCjk(char c) => c
        is >= '　' and <= '〿'    // 中日标点
        or >= '぀' and <= 'ヿ'    // 平假名 / 片假名
        or >= '㐀' and <= '䶿'    // 扩展 A
        or >= '一' and <= '鿿'    // 基本区
        or >= '豈' and <= '﫿'    // 兼容表意
        or >= '＀' and <= '･';   // 全角字符

    static bool EndsHyphenated(StringBuilder sb) =>
        sb.Length >= 2 && sb[^1] == '-' && char.IsLetter(sb[^2]);

    static OcrEngine? EngineFor(string tag)
    {
        lock (Gate)
        {
            if (Engines.TryGetValue(tag, out var hit)) return hit;
            OcrEngine? engine = null;
            try
            {
                engine = OcrEngine.TryCreateFromLanguage(new Language(tag))
                      ?? OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch (Exception ex)
            {
                Log.Warn($"创建 OCR 引擎失败（{tag}）：" + ex.Message);
            }
            Engines[tag] = engine;
            return engine;
        }
    }

    /// <summary>
    /// 语言标签给人看的名字，比如 zh-Hans-CN → 「中文（简体，中国）」。
    /// 不走 Languages.NameOf：系统的标签比本程序的语言代码长（多了脚本和地区），
    /// 查不到只会把原标签吐回来。
    /// </summary>
    public static string DisplayName(string tag)
    {
        try
        {
            var name = new Language(tag).DisplayName;
            return string.IsNullOrWhiteSpace(name) ? tag : name;
        }
        catch { return tag; }
    }

    public static string NoEngineHint() =>
        "系统里没有可用的文字识别语言包。到「设置 → 时间和语言 → 语言 → 选中语言 → 选项 → " +
        "可选功能」里装上「光学字符识别」，然后重开本程序。";
}
