using System.IO;
using System.Runtime.InteropServices;
using FlashTrans.Interop;
using RapidOcrNet;
using SkiaSharp;
using Windows.Globalization;

namespace FlashTrans.Services;

public static partial class OcrService
{
    // ---- RapidOCR（本地 ONNX 模型）----
    // 模型文件放在 exe 同目录 models\v6\ 下，随发布包分发（Apache-2.0）。
    // tiny 识别词表不含日语假名；保留轻量检测器，识别必须搭配 small 及其原始词表。
    const string RapidModelsRoot = "models";
    const string RapidModelsVersion = "v6";

    static readonly string[] RapidModelFiles =
    [
        "PP-OCRv6_det_tiny.onnx",
        "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
        "PP-OCRv6_rec_small.onnx",
        "ppocrv6_small_dict.txt",
    ];

    static readonly object RapidGate = new();
    static RapidOcr? _rapid;
    static bool _rapidTried;

    /// <summary>RapidOCR 模型文件是否齐全（只查文件，不触发加载）。</summary>
    public static bool RapidModelsPresent
    {
        get
        {
            var dir = RapidModelsDir;
            return dir is not null && RapidModelFiles.All(f => File.Exists(Path.Combine(dir, f)));
        }
    }

    /// <summary>缺了哪些模型文件，给设置页和报错文案用。</summary>
    public static string? RapidModelsHint()
    {
        var dir = RapidModelsDir;
        if (dir is null)
            return $"找不到模型目录：{Path.Combine(AppContext.BaseDirectory, RapidModelsRoot, RapidModelsVersion)}";
        var missing = RapidModelFiles.Where(f => !File.Exists(Path.Combine(dir, f))).ToArray();
        return missing.Length == 0 ? null : $"模型目录缺文件：{string.Join("、", missing)}";
    }

    static string? RapidModelsDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, RapidModelsRoot, RapidModelsVersion);
            return Directory.Exists(dir) ? dir : null;
        }
    }

    /// <summary>懒加载 RapidOCR。失败记日志返回 null，让调用方回退系统 OCR。</summary>
    static RapidOcr? RapidEngine()
    {
        lock (RapidGate)
        {
            if (_rapidTried) return _rapid;
            _rapidTried = true;
            var dir = RapidModelsDir;
            if (dir is null) return null;
            try
            {
                var ocr = new RapidOcr();
                ocr.InitModels(new RapidOcrModelSet
                {
                    DetModelPath = Path.Combine(dir, "PP-OCRv6_det_tiny.onnx"),
                    ClsModelPath = Path.Combine(dir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                    RecModelPath = Path.Combine(dir, "PP-OCRv6_rec_small.onnx"),
                    KeysPath = Path.Combine(dir, "ppocrv6_small_dict.txt"),
                    DetMean = [127.5F, 127.5F, 127.5F],
                    DetStd = [127.5F, 127.5F, 127.5F],
                });
                _rapid = ocr;
            }
            catch (Exception ex)
            {
                Log.Warn("初始化 RapidOCR 失败：" + ex.Message);
            }
            return _rapid;
        }
    }

    // 文本提取和打码共用路由，但打码仍用原始检测框，不能从纠偏后的文字猜坐标。
    static async Task<(List<Token>? RapidTokens, string? SystemLanguage)> RouteRecognitionAsync(
        CapturedImage image, string? preferred, string? engine, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fallbackLanguages = RapidFallbackLanguages(preferred, AvailableLanguages);
        if (SystemFallbackScript(preferred) is not null)
        {
            if (fallbackLanguages.Length == 0)
                throw new InvalidOperationException($"未安装「{DisplayName(preferred!)}」的 Windows OCR 语言包。"
                    + "请在 Windows 设置 → 时间和语言 → 语言选项中安装「光学字符识别」，然后重启闪译；不会改用其他语言包冒充识别。");
            return (null, fallbackLanguages[0]);
        }
        if (string.Equals(engine, "system", StringComparison.OrdinalIgnoreCase)) return (null, preferred);
        if (!string.Equals(engine, "rapid", StringComparison.OrdinalIgnoreCase) && RapidEngine() is null)
            return (null, preferred);

        var tokens = await RecognizeRapidTokensAsync(image, ct).ConfigureAwait(false);
        if (fallbackLanguages.Length == 0) return (tokens, null);

        // Rapid 不认识的文字也可能被误读成非空中文，不能只在空结果时才探测。
        // 只试已安装且不在模型能力内的脚本，每种语言一遍，选中后才跑系统增强通道。
        var maxDimension = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;
        if (image.Width > maxDimension || image.Height > maxDimension) return (tokens, null);
        var scaled = image.ScaleUpForOcr(640, 8, maxDimension);
        var language = await ChooseSystemFallbackAsync(ComposeRapidText(tokens), fallbackLanguages,
            async (tag, token) => EngineFor(tag) is { } ocr
                ? await RecognizeBitmapAsync(ocr, scaled, tag, token).ConfigureAwait(false) : "", ct)
            .ConfigureAwait(false);
        return language is not null ? (null, language) : (tokens, null);
    }

    internal static async Task<string?> ChooseSystemFallbackAsync(string rapidText, IEnumerable<string> languages,
        Func<string, CancellationToken, Task<string>> recognize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RecognitionCandidate? best = null;
        foreach (var tag in languages)
        {
            ct.ThrowIfCancellationRequested();
            var text = await recognize(tag, ct).ConfigureAwait(false);
            if (!ShouldUseSystemFallback(rapidText, text, tag)) continue;
            if (best is null || Score(text, tag) > Score(best.Value.Text, best.Value.Language))
                best = new RecognitionCandidate(text, tag);
        }
        ct.ThrowIfCancellationRequested();
        return best?.Language;
    }

    static string ComposeRapidText(List<Token> tokens) =>
        NormalizeOcrText(CleanCandidate(LayoutTokens(tokens, cjk: true, rightToLeft: false)));

    internal static string[] RapidFallbackLanguages(string? preferred, IEnumerable<string> installed)
    {
        var candidates = installed.Where(tag => SystemFallbackScript(tag) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        if (IsAutomaticLanguage(preferred))
            return candidates.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToArray();
        var script = SystemFallbackScript(preferred);
        if (script is null) return [];
        return candidates.Where(tag => tag.Split('-')[0].Equals(preferred!.Split('-')[0],
                StringComparison.OrdinalIgnoreCase) && SystemFallbackScript(tag) == script)
            .OrderByDescending(tag => tag.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            .ThenBy(tag => tag, StringComparer.OrdinalIgnoreCase).Take(1).ToArray();
    }

    static string? SystemFallbackScript(string? tag)
    {
        if (IsAutomaticLanguage(tag)) return null;
        try
        {
            // 用系统语言元数据区分 sr-Cyrl / sr-Latn，不把整个语言前缀一刀切。
            return new Language(tag!).Script switch
            {
                "Kore" or "Hang" => "Hang",
                "Cyrl" => "Cyrl",
                "Arab" => "Arab",
                "Thai" => "Thai",
                "Grek" => "Grek",
                "Hebr" => "Hebr",
                "Deva" => "Deva",
                _ => null,
            };
        }
        catch (ArgumentException) { return null; }
    }

    internal static bool ShouldUseSystemFallback(string rapidText, string systemText, string language)
    {
        var script = SystemFallbackScript(language);
        var letters = systemText.Count(char.IsLetter);
        var matching = systemText.Count(c => char.IsLetter(c) && (script switch
        {
            "Hang" => IsHangul(c),
            "Cyrl" => IsCyrillic(c),
            "Arab" => IsArabic(c),
            "Thai" => IsThai(c),
            "Grek" => IsGreek(c),
            "Hebr" => IsHebrew(c),
            "Deva" => IsDevanagari(c),
            _ => false,
        }));
        // 单个异文字符、公式变量或少量噪声不足以切换引擎；比较结果而不是假定包名即真值。
        return matching >= 2 && matching * 5 >= letters * 3
            && Score(systemText, language) > Score(rapidText);
    }

    static readonly SemaphoreSlim RapidRecognitionGate = new(1, 1);

    static async Task<List<Token>> RecognizeRapidTokensAsync(CapturedImage image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var engine = RapidEngine();
        if (engine is null)
            throw new InvalidOperationException(
                "RapidOCR 模型没就位：" + (RapidModelsHint() ?? "初始化失败") +
                "。去「设置 → OCR」改回系统 OCR，或补上模型文件。");

        ct.ThrowIfCancellationRequested();
        // Detect 是 CPU 密集的同步调用，挪到线程池免得卡住界面线程。
        // 取消不会中断已进入的原生推理，真正结束后才释放共享引擎，避免下一次截图并发访问。
        await RapidRecognitionGate.WaitAsync(ct).ConfigureAwait(false);
        RapidOcrNet.OcrResult result;
        try
        {
            using var bmp = ToSkiaBitmap(image);
            result = await Task.Run(() => engine.Detect(bmp, RapidOcrOptions.PPOCRv6), ct)
                .ConfigureAwait(false);
        }
        finally { RapidRecognitionGate.Release(); }
        ct.ThrowIfCancellationRequested();

        var tokens = new List<Token>();
        foreach (var block in result.TextBlocks)
        {
            var text = block.Text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            var pts = block.BoxPoints;
            var left = pts.Min(p => p.X);
            var top = pts.Min(p => p.Y);
            var right = pts.Max(p => p.X);
            var bottom = pts.Max(p => p.Y);
            tokens.Add(new Token(text, left, top,
                Math.Max(1, right - left), Math.Max(1, bottom - top), tokens.Count));
        }
        return tokens;
    }

    /// <summary>把 BGRA32 像素搬进 SKBitmap（SkiaSharp 原生 BGRA 序，行宽一致直接拷）。</summary>
    static SKBitmap ToSkiaBitmap(CapturedImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        try
        {
            Marshal.Copy(image.Pixels, 0, bmp.GetPixels(), image.Pixels.Length);
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }
}
