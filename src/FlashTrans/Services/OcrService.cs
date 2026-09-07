using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
    readonly record struct RecognitionCandidate(string Text, string Language);
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
        ct.ThrowIfCancellationRequested();
        var engines = RecognitionLanguages(preferred)
            .Select(tag => (Tag: tag, Engine: EngineFor(tag)))
            .Where(candidate => candidate.Engine is not null).ToList();
        if (engines.Count == 0) throw new InvalidOperationException(NoEngineHint());

        // 小字号和细笔画是系统 OCR 最容易漏掉的部分。专用最近邻放大不插值，
        // 先保住数字边缘；第二遍再用灰度高对比图补一遍。
        var img = image.ScaleUpForOcr(640, 8, (int)OcrEngine.MaxImageDimension);
        if (img.Width > OcrEngine.MaxImageDimension || img.Height > OcrEngine.MaxImageDimension)
            throw new InvalidOperationException(
                $"截取的区域太大（上限 {OcrEngine.MaxImageDimension} 像素），选小一点");

        ct.ThrowIfCancellationRequested();

        var candidates = new List<RecognitionCandidate>();
        foreach (var (tag, engine) in engines)
        {
            ct.ThrowIfCancellationRequested();
            var text = await RecognizeBitmapAsync(engine!, img, tag, ct).ConfigureAwait(false);
            text = CleanCandidate(text);
            if (!string.IsNullOrWhiteSpace(text)) candidates.Add(new RecognitionCandidate(text, tag));
        }

        // 先用原图在各语言中定出最匹配的脚本，再只对该语言跑增强通道。
        // 不然装了五六个语言包的人一次截图会触发十几次 OCR，准确率没多多少，等待却很肉。
        // 原图无文字仍尝试增强，但不能把空白截图误报成没有语言包。
        var baseBest = candidates.Count == 0 ? new RecognitionCandidate("", engines[0].Tag)
            : candidates.OrderByDescending(c => Score(c.Text, c.Language)).First();
        var bestEngine = EngineFor(baseBest.Language)!;
        var source = image;
        // 仅对内容很少的中小竖图尝试横转，且必须显著胜过原方向。
        // 正常截图、多行长截图不会为方向探测额外跑一整套语言和增强通道。
        if (image.Height > image.Width * 1.2 && image.Width >= 64
            && (long)image.Width * image.Height <= 1_000_000
            && baseBest.Text.Count(char.IsLetterOrDigit) < 80)
        {
            var threshold = Math.Max(40, Score(baseBest.Text, baseBest.Language) * 2);
            foreach (var clockwise in new[] { true, false })
            {
                ct.ThrowIfCancellationRequested();
                var rotatedSource = image.RotateForOcr(clockwise);
                var rotated = rotatedSource.ScaleUpForOcr(640, 8, (int)OcrEngine.MaxImageDimension);
                var text = CleanCandidate(await RecognizeBitmapAsync(bestEngine, rotated, baseBest.Language, ct)
                    .ConfigureAwait(false));
                var score = Score(text, baseBest.Language);
                if (score <= threshold) continue;
                threshold = score;
                baseBest = new RecognitionCandidate(text, baseBest.Language);
                source = rotatedSource;
                img = rotated;
            }
            if (!ReferenceEquals(source, image))
            {
                // 不同方向的行坐标没有对应关系，不能把原方向的噪声符号合进正确段落。
                candidates.Clear();
                candidates.Add(baseBest);
            }
        }
        var enhanced = img.EnhanceForOcr();
        for (var pass = 0; pass < 3; pass++)
        {
            ct.ThrowIfCancellationRequested();
            // 逐个创建，避免大图同时保留所有增强副本。
            var variant = pass switch
            {
                0 => enhanced,
                1 => enhanced.BinarizeForOcr(),
                _ => source.SmoothForOcr((int)OcrEngine.MaxImageDimension),
            };
            var text = await RecognizeBitmapAsync(bestEngine, variant, baseBest.Language, ct)
                .ConfigureAwait(false);
            text = CleanCandidate(text);
            if (!string.IsNullOrWhiteSpace(text))
                candidates.Add(new RecognitionCandidate(text, baseBest.Language));
        }

        if (candidates.Count == 0) return "";
        var best = candidates.OrderByDescending(c => Score(c.Text, c.Language)).First();
        return NormalizeOcrText(MergeSymbols(best.Text, candidates.Select(c => c.Text)));
    }

    static bool IsAutomaticLanguage(string? preferred) =>
        string.IsNullOrWhiteSpace(preferred)
        || string.Equals(preferred, Core.Languages.Auto, StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> RecognitionLanguages(string? preferred)
    {
        if (!IsAutomaticLanguage(preferred))
        {
            var tag = ResolveLanguage(preferred);
            if (tag is not null) yield return tag;
            yield break;
        }

        // 英文界面优先用英文包；没有英文包时自然退回中文/日文等已安装语言。
        var langs = AvailableLanguages;
        foreach (var tag in langs
                     .OrderBy(LanguagePriority)
                     .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            yield return tag;
    }

    static int LanguagePriority(string tag)
    {
        if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return 0;
        if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return 1;
        if (tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    static async Task<string> RecognizeBitmapAsync(OcrEngine engine, CapturedImage image,
                                                    string tag, CancellationToken ct)
    {
        using var bitmap = ToSoftwareBitmap(image);
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
        return Compose(result, tag);
    }

    static string CleanCandidate(string text)
    {
        // 代码可能只截取了一部分；没有像素证据就不能补括号或猜改 Id、sha1sum 等标识符。
        return LooksLikeIdentifierBlock(text) ? text : NormalizeConfusables(text);
    }

    static bool LooksLikeIdentifierBlock(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var identifiers = lines.Count(line => line.Length >= 4
            && line.All(c => IsAsciiLetter(c) || char.IsDigit(c) || c is '_' or '-' or '{' or '}' or ':' or ','));
        return text.Contains('{') || text.Contains('}') || identifiers >= 4;
    }

    static int Score(string text, string? language = null)
    {
        var score = 0;
        var han = 0;
        var kana = 0;
        var hangul = 0;
        var cyrillic = 0;
        var arabic = 0;
        var latin = 0;
        var greek = 0;
        var hebrew = 0;
        var thai = 0;
        var devanagari = 0;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) score += 3;
            else if (IsMeaningfulSymbol(c)) score += 2;
            else if (char.IsPunctuation(c)) score++;
            else if (!char.IsWhiteSpace(c)) score -= 4;
            if (IsHan(c)) han++;
            else if (IsKana(c)) kana++;
            else if (IsHangul(c)) hangul++;
            else if (IsCyrillic(c)) cyrillic++;
            else if (IsArabic(c)) arabic++;
            else if (IsGreek(c)) greek++;
            else if (IsHebrew(c)) hebrew++;
            else if (IsThai(c)) thai++;
            else if (IsDevanagari(c)) devanagari++;
            else if (IsLatin(c)) latin++;
        }
        score += Math.Min(30, text.Count(c => c == '\n') * 2);
        if (language is not null)
        {
            if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) score += Math.Min(60, han * 2);
            else if (language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) score += Math.Min(60, kana * 3 + han);
            else if (language.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) score += Math.Min(60, hangul * 3);
            else if (language.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
                     || language.StartsWith("uk", StringComparison.OrdinalIgnoreCase)) score += Math.Min(60, cyrillic * 3);
            else if (language.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
                     || language.StartsWith("fa", StringComparison.OrdinalIgnoreCase)) score += Math.Min(60, arabic * 3);
            else if (language.StartsWith("el", StringComparison.OrdinalIgnoreCase)) score += Math.Min(60, greek * 3);
            else if (language.StartsWith("he", StringComparison.OrdinalIgnoreCase)) score += Math.Min(60, hebrew * 3);
            else if (language.StartsWith("th", StringComparison.OrdinalIgnoreCase)) score += Math.Min(60, thai * 3);
            else if (language.StartsWith("hi", StringComparison.OrdinalIgnoreCase)
                     || language.StartsWith("mr", StringComparison.OrdinalIgnoreCase)
                     || language.StartsWith("ne", StringComparison.OrdinalIgnoreCase)) score += Math.Min(60, devanagari * 3);
            else score += Math.Min(60, latin * 2);
        }
        return score;
    }

    internal static int LanguageScoreForTest(string text, string language) => Score(text, language);

    /// <summary>在多通道结果中合并相同正文骨架下额外识别到的特殊符号。</summary>
    internal static string MergeSymbols(string primary, IEnumerable<string> alternatives)
    {
        var lines = primary.Split('\n');
        foreach (var alternative in alternatives)
        {
            var candidates = alternative.Split('\n');
            // 没有可靠的行对应关系时宁可保留主结果，不能把同名字段的符号串到另一行。
            if (candidates.Length != lines.Length) continue;
            for (var i = 0; i < lines.Length; i++)
            {
                var candidate = candidates[i];
                var skeleton = Skeleton(candidate);
                if (skeleton.Length == 0) continue;
                if (!string.Equals(Skeleton(lines[i]), skeleton, StringComparison.Ordinal)
                    || NumberSignature(lines[i]) != NumberSignature(candidate)
                    || !PreservesSymbols(lines[i], candidate)
                    || SymbolScore(candidate) <= SymbolScore(lines[i])) continue;

                var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                lines[i] = indent + candidate.TrimStart();
            }
        }
        return string.Join('\n', lines);
    }

    static string NumberSignature(string text) => string.Join("|",
        Regex.Matches(text, @"[-+]?\d+(?:[.,]\d+)*").Select(match => match.Value));

    static bool PreservesSymbols(string original, string candidate)
    {
        var remaining = original.Where(IsMeaningfulSymbol).ToArray();
        var index = 0;
        foreach (var symbol in candidate.Where(IsMeaningfulSymbol))
            if (index < remaining.Length && remaining[index] == symbol) index++;
        return index == remaining.Length;
    }

    /// <summary>
    /// 统一 OCR 常见的 Unicode/标点拆分错误。
    /// 不做全局 l/1 猜测，也不删除 emoji；只在数字、单位和标点上下文明确时收紧空格。
    /// </summary>
    internal static string NormalizeOcrText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // NFKC 会把 cm²、½、① 等有意义的字符改写；这里只折叠全角 ASCII。
        var normalized = new string(text.Normalize(NormalizationForm.FormC)
            .Select(c => c is >= '\uFF01' and <= '\uFF5E' ? (char)(c - 0xFEE0)
                : c == '\u3000' ? ' ' : c).ToArray());
        var lines = normalized
            .Replace('\u2018', '\'')
            .Replace('\u2019', '\'')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"')
            .Replace('\u2215', '/')
            .Replace('\u2044', '/')
            .ReplaceLineEndings("\n")
            .Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            line = Regex.Replace(line, @"(?<=\d)[ \t]*[.][ \t]*(?=\d)", ".");
            line = Regex.Replace(line, @"(?<=\d)[ \t]+(?=%)", "");
            line = Regex.Replace(line, @"(?<=\d)[ \t]+([KMGT])[ \t]+B\b", " $1B");
            line = Regex.Replace(line, @"(?<=\d)[ \t]+1['’\u0060·•]?IB(?![A-Za-z])", " MB");
            line = Regex.Replace(line, @"(\d[ \t]+(?:[KMGT]i?B|B))[ \t]*/[ \t]*(?=\d)", "$1/");
            // 保留标点后的单词间隔，以及代码/列表的首行缩进。
            line = Regex.Replace(line, @"(?<=\S)[ \t]+(?=[,;:!?])", "");
            // Windows 中文 OCR 对极小字号的部件字有几个稳定混淆，
            // 只在明确的词组里修正，避免对普通中文做全局替换。
            line = Regex.Replace(line, @"(?<!\p{L})文亻牛(?!\p{L})", "文件");
            line = Regex.Replace(line, @"(?<!\p{L})文件荚(?!\p{L})", "文件夹");
            line = Regex.Replace(line, @"(?<!\p{L})分旱(?!\p{L})", "分享");
            lines[i] = line;
        }

        return string.Join('\n', lines).TrimEnd();
    }

    static string Skeleton(string text) => Regex.Replace(new string(text
        .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray()), @"\s+", " ").Trim();
    static int SymbolScore(string text) => text.Count(IsMeaningfulSymbol);
    static bool IsMeaningfulSymbol(char c) => c is
        '{' or '}' or '[' or ']' or '(' or ')' or '<' or '>' or ':' or ';' or ',' or '.' or
        '_' or '-' or '+' or '=' or '*' or '/' or '\\' or '|' or '&' or '@' or '#' or '$' or
        '%' or '^' or '~' or '!' or '?' or '…' or '→' or '←' or '↑' or '↓' or '≤' or '≥' or
        '≠' or '±' or '×' or '÷';

    /// <summary>
    /// 只修上下文明确的 ASCII 混淆：字母之间的孤立 1→l，数字之间的 l→1。
    /// 不做全局替换，避免把真实数字或字母改坏。
    /// </summary>
    internal static string NormalizeConfusables(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if ((chars[i] == 'l' || chars[i] == 'I') && i > 0 && i + 1 < chars.Length
                && char.IsDigit(chars[i - 1]) && char.IsDigit(chars[i + 1]))
            {
                chars[i] = '1';
                continue;
            }

            if ((chars[i] != '1' && chars[i] != 'I') || i == 0 || i + 1 >= chars.Length
                || !IsAsciiLetter(chars[i - 1]) || !IsAsciiLetter(chars[i + 1])) continue;

            var left = i;
            while (left > 0 && IsAsciiLetter(chars[left - 1])) left--;
            var right = i + 1;
            while (right + 1 < chars.Length && IsAsciiLetter(chars[right + 1])) right++;
            // 短标识（如 a1b）保留原样；较长单词中的孤立 1 才按 l 处理。
            if (right - left + 1 >= 4 && chars[i] == '1') chars[i] = 'l';
        }
        return new string(chars);
    }

    static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

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

    readonly record struct Token(string Text, double Left, double Top, double Width, double Height, int Order);
    readonly record struct OcrRow(List<Token> Tokens, double Top, double Bottom);

    /// <summary>
    /// 不直接使用 OcrLine.Text：系统 OCR 有时会把垂直列表误合成一行。
    /// 用每个词的 BoundingRect 重建行，能保住换行、缩进和特殊符号之间的间距。
    /// </summary>
    static string Compose(OcrResult result, string tag)
    {
        var lines = result.Lines;
        if (lines.Count == 0) return "";

        var cjk = tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
               || tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        var rightToLeft = CultureInfo.GetCultureInfo(tag).TextInfo.IsRightToLeft;

        var tokens = new List<Token>();
        foreach (var line in lines)
        {
            foreach (var word in line.Words)
            {
                var text = word.Text?.Trim();
                var rect = word.BoundingRect;
                if (!string.IsNullOrEmpty(text) && rect.Width >= 0 && rect.Height >= 0)
                    tokens.Add(new Token(text, rect.X, rect.Y, rect.Width, rect.Height, tokens.Count));
            }

            // 极少数系统版本会返回没有 Words 的行，至少保住它的整行文本。
            if (line.Words.Count == 0 && !string.IsNullOrWhiteSpace(line.Text))
                tokens.Add(new Token(line.Text.Trim(), 0, tokens.Count * 1000, 0, 0, tokens.Count));
        }
        if (tokens.Count == 0) return "";

        return LayoutTokens(tokens, cjk, rightToLeft);
    }

    internal static string LayoutTokensForTest(
        IEnumerable<(string Text, double Left, double Top, double Width, double Height)> tokens,
        bool cjk = false, bool rightToLeft = false) => LayoutTokens(tokens.Select((t, i) =>
            new Token(t.Text, t.Left, t.Top, t.Width, t.Height, i)).ToList(), cjk, rightToLeft);

    static string LayoutTokens(List<Token> tokens, bool cjk, bool rightToLeft)
    {
        if (tokens.Count == 0) return "";
        var rows = GroupRows(tokens, rightToLeft);
        var typical = rows.Select(r => r.Bottom - r.Top).Where(h => h > 0).DefaultIfEmpty(0).Average();
        var widths = tokens.Where(t => t.Width > 0 && t.Text.Length > 0)
            .Select(t => t.Width / Math.Max(1, t.Text.Length)).OrderBy(w => w).ToArray();
        var charWidth = widths.Length == 0 ? 8 : Math.Max(2, widths[widths.Length / 2]);
        var minLeft = tokens.Min(t => t.Left);
        var sb = new StringBuilder();
        OcrRow? previous = null;
        foreach (var row in rows)
        {
            var text = JoinRow(row.Tokens, cjk);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (previous is not null)
            {
                var gap = row.Top - previous.Value.Bottom;
                sb.Append(gap > typical * 1.8 ? "\n\n" : "\n");
            }
            var indent = Math.Clamp((int)Math.Round((row.Tokens.Min(t => t.Left) - minLeft) / charWidth), 0, 24);
            if (indent > 0) sb.Append(' ', indent);
            sb.Append(text);
            previous = row;
        }
        return sb.ToString();
    }

    static List<OcrRow> GroupRows(List<Token> tokens, bool rightToLeft)
    {
        var heights = tokens.Select(t => t.Height).Where(h => h > 0).OrderBy(h => h).ToArray();
        var median = heights.Length == 0 ? 16 : heights[heights.Length / 2];
        var tolerance = Math.Max(4, median * 0.55);
        var rows = new List<List<Token>>();

        foreach (var token in tokens.OrderBy(t => t.Top).ThenBy(t => t.Left))
        {
            var center = token.Top + token.Height / 2;
            var row = rows
                .Select((r, i) => (Row: r, Index: i, Distance: Math.Abs(center - RowCenter(r))))
                .Where(x => x.Distance <= tolerance)
                .OrderBy(x => x.Distance)
                .FirstOrDefault();
            if (row.Row is null) rows.Add([token]);
            else row.Row.Add(token);
        }

        return rows
            // RTL 沿用系统返回的逻辑词序，不能简单反转坐标顺序，否则混排的英文和数字也会倒置。
            .Select(r => new OcrRow((rightToLeft ? r.OrderBy(t => t.Order) : r.OrderBy(t => t.Left)).ToList(),
                r.Min(t => t.Top), r.Max(t => t.Top + t.Height)))
            .OrderBy(r => r.Top)
            .ToList();
    }

    static double RowCenter(List<Token> row) => row.Average(t => t.Top + t.Height / 2);

    static string JoinRow(List<Token> tokens, bool cjk)
    {
        var sb = new StringBuilder();
        Token? previous = null;
        foreach (var token in tokens)
        {
            if (sb.Length > 0 && previous is { } prev)
            {
                var gap = Math.Max(token.Left - (prev.Left + prev.Width),
                    prev.Left - (token.Left + token.Width));
                var left = token.Text[0];
                var right = prev.Text[^1];
                var height = Math.Max(prev.Height, token.Height);
                var cjkGap = cjk && IsCjk(right) && IsCjk(left);
                var needsSpace = gap > Math.Max(1.5, height * 0.12)
                                 && !IsClosingPunctuation(left)
                                 && !IsOpeningPunctuation(right)
                                 && (!cjkGap || gap > height * 0.8);
                if (needsSpace)
                {
                    var charWidth = Math.Max(2, (prev.Width / Math.Max(1, prev.Text.Length)
                                                + token.Width / Math.Max(1, token.Text.Length)) / 2);
                    sb.Append(' ', Math.Clamp((int)Math.Round(gap / charWidth), 1, 8));
                }
            }
            sb.Append(token.Text);
            previous = token;
        }
        return sb.ToString().Trim();
    }

    static bool IsOpeningPunctuation(char c) => c is '(' or '[' or '{' or '<';
    static bool IsClosingPunctuation(char c) => c is ')' or ']' or '}' or '>' or ',' or '.' or ':' or ';' or '!' or '?';

    static bool IsCjk(char c) => IsHan(c) || IsKana(c)
        || c is >= '　' and <= '〿' or >= '＀' and <= '･';
    static bool IsHan(char c) => c is >= '㐀' and <= '䶿'
        or >= '一' and <= '鿿' or >= '豈' and <= '﫿';
    static bool IsKana(char c) => c is >= '぀' and <= 'ヿ';
    static bool IsHangul(char c) => c is >= '가' and <= '힣' or >= 'ㄱ' and <= 'ㆎ';
    static bool IsCyrillic(char c) => c is >= 'Ѐ' and <= 'ӿ';
    static bool IsArabic(char c) => c is >= '؀' and <= 'ۿ';
    static bool IsGreek(char c) => c is >= 'Ͱ' and <= 'Ͽ';
    static bool IsHebrew(char c) => c is >= '֐' and <= '׿';
    static bool IsThai(char c) => c is >= 'ก' and <= '฿';
    static bool IsDevanagari(char c) => c is >= 'ऀ' and <= 'ॿ';
    static bool IsLatin(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
        or >= 'À' and <= 'ɏ';

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
