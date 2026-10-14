using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace FlashTrans.Services;

/// <summary>只在本机判断候选区域；结果不携带原文，避免 UI、日志或配置意外留存敏感内容。</summary>
internal static class SensitiveContentDetector
{
    internal const int MaxKeywordsLength = 4096;
    static Regex Rule(string pattern) => new(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));

    static readonly Regex Email = Rule(@"[a-z0-9.!#$%&'*+/=?^_{|}~-]+@[a-z0-9-]+(?:\.[a-z0-9-]+)+");
    static readonly Regex Phone = Rule(@"(?<![0-9])(?:\+?86[ \t-]*)?1[3-9](?:[ \t-]*[0-9]){9}(?![0-9])");
    static readonly Regex Identity = Rule(@"(?<![0-9])[1-8][0-9]{16}[0-9x](?![0-9])");
    static readonly Regex Card = Rule(@"(?<![0-9])[0-9]{13,19}(?![0-9])");
    static readonly Regex Secret = Rule(
        @"\b(?:sk-(?:proj-)?[a-z0-9_-]{16,}|gh[pousr]_[a-z0-9]{20,}|github_pat_[a-z0-9_]{20,}|AKIA[A-Z0-9]{16})\b"
        + @"|\beyJ[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\b"
        + @"|\bBearer\s+[a-z0-9._~+/-]{12,}=*");
    static readonly Regex LabelledSecret = Rule(
        @"(?:\b(?:password|passwd|pwd|api[ _-]?key|access[ _-]?token|token|secret|otp)\b|密码|口令|密钥|验证码)"
        + @"[""']?(?:\s*[:=]\s*|\s+)[""']?[^\s""',;]+");

    internal static IReadOnlyList<Rect> Detect(IReadOnlyList<OcrTextRegion> regions,
        int width, int height, string? customKeywords = null, CancellationToken ct = default)
    {
        var keywords = Keywords(customKeywords);
        if (regions.Count > 4096 || regions.Sum(r => (long)r.Text.Length) > 500_000)
            throw new InvalidOperationException("识别内容过多，请缩小选区后重试。");
        var rows = new List<OcrTextRegion>();
        foreach (var region in regions.OrderBy(r => r.Bounds.Top).ThenBy(r => r.Bounds.Left))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(region.Text)) continue;
            var b = region.Bounds;
            if (!ValidBounds(b) || !b.IntersectsWith(new Rect(0, 0, width, height)))
                throw new InvalidOperationException("OCR 文字位置无效，无法可靠打码，请手动遮挡。");
            // 某些引擎把标签和值分成相邻的框。只合并同基线、距离较近的块，不跨行拼数字。
            var at = rows.FindLastIndex(r => SameRow(r.Bounds, b));
            if (at < 0) rows.Add(region);
            else
            {
                var previous = rows[at];
                var text = b.Left >= previous.Bounds.Left
                    ? previous.Text + " " + region.Text : region.Text + " " + previous.Text;
                rows[at] = new OcrTextRegion(text, Rect.Union(previous.Bounds, b));
            }
        }

        var result = new List<Rect>();
        // 原块也要判断：跨语言识别框重复时，合并后的噪声不能掩盖原本有效的号码。
        foreach (var row in regions.Concat(rows))
        {
            ct.ThrowIfCancellationRequested();
            if (!ContainsSensitive(row.Text, keywords)) continue;
            var b = row.Bounds;
            var padding = Math.Clamp(b.Height * .15, 2, 8);
            b.Inflate(padding, padding);
            var left = Math.Clamp(Math.Floor(b.Left), 0, width);
            var top = Math.Clamp(Math.Floor(b.Top), 0, height);
            var right = Math.Clamp(Math.Ceiling(b.Right), 0, width);
            var bottom = Math.Clamp(Math.Ceiling(b.Bottom), 0, height);
            var area = new Rect(left, top, right - left, bottom - top);
            if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) continue;
            // 去除多语言和重叠框的重复候选；向外合并而不是缩小，避免丢掉文字边缘。
            for (var i = result.Count - 1; i >= 0; i--)
                if (area.IntersectsWith(result[i]))
                {
                    area.Union(result[i]);
                    result.RemoveAt(i);
                }
            result.Add(area);
        }
        return result;
    }

    static bool ValidBounds(Rect b) => !b.IsEmpty && b.Width > 0 && b.Height > 0
        && double.IsFinite(b.X) && double.IsFinite(b.Y)
        && double.IsFinite(b.Right) && double.IsFinite(b.Bottom);

    static bool SameRow(Rect a, Rect b)
    {
        var overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        var gap = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right);
        return overlap >= Math.Min(a.Height, b.Height) * .65
            && gap <= Math.Max(a.Height, b.Height) * 3;
    }

    internal static string[] Keywords(string? text)
    {
        text ??= "";
        if (text.Length > MaxKeywordsLength)
            throw new InvalidOperationException($"自定义关键词最多 {MaxKeywordsLength} 个字符。");
        return text.Split([';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Normalize(s).Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool ContainsSensitive(string text, IReadOnlyList<string>? keywords = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normal = Normalize(text);
        if (keywords is not null && keywords.Any(k => normal.Contains(k, StringComparison.OrdinalIgnoreCase))) return true;
        if (Secret.IsMatch(normal) || LabelledSecret.IsMatch(normal)) return true;
        // 仅用于格式匹配，不回写原文、不猜 O/0 或 I/1；全角和排版空格不应绕过规则。
        var compact = new string(normal.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (keywords is not null && keywords.Any(k => compact.Contains(
                new string(k.Where(c => !char.IsWhiteSpace(c)).ToArray()), StringComparison.OrdinalIgnoreCase))) return true;
        if (Secret.IsMatch(compact) || LabelledSecret.IsMatch(compact)) return true;
        if (Email.IsMatch(compact)) return true;
        var numbers = compact.Replace("-", "").Replace("–", "").Replace("—", "");
        // 原始分隔也必须检查；只看去空格版本会把同一行的两个号码连成一个长整数而漏检。
        foreach (var candidate in new[] { normal, numbers })
        {
            if (Phone.IsMatch(candidate)) return true;
            foreach (Match match in Identity.Matches(candidate))
                if (ValidIdentity(match.Value)) return true;
            foreach (Match match in Card.Matches(candidate))
                if (ValidCard(match.Value)) return true;
        }
        return false;
    }

    static string Normalize(string text) => new(text.Normalize(NormalizationForm.FormKC)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray());

    internal static bool ValidIdentity(string value)
    {
        if (value.Length != 18 || !value.AsSpan(0, 17).ToString().All(char.IsAsciiDigit)) return false;
        if (!DateOnly.TryParseExact(value.AsSpan(6, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var birthday)
            || birthday.Year < 1800 || birthday > DateOnly.FromDateTime(DateTime.Today)) return false;
        int[] weights = [7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2];
        var sum = 0;
        for (var i = 0; i < 17; i++) sum += (value[i] - '0') * weights[i];
        return char.ToUpperInvariant(value[17]) == "10X98765432"[sum % 11];
    }

    internal static bool ValidCard(string value)
    {
        if (value.Length is < 13 or > 19 || !value.All(char.IsAsciiDigit)
            || value.All(c => c == value[0])) return false;
        // 限定常见卡号前缀并用 Luhn 校验，不把任意长整数都当银行卡。
        var common = value[0] == '4' && value.Length is 13 or 16 or 19
            || value.StartsWith("62", StringComparison.Ordinal) && value.Length >= 16
            || (value.StartsWith("34", StringComparison.Ordinal) || value.StartsWith("37", StringComparison.Ordinal)) && value.Length == 15
            || value.Length == 16 && (int.Parse(value[..2], CultureInfo.InvariantCulture) is >= 51 and <= 55
                || int.Parse(value[..4], CultureInfo.InvariantCulture) is >= 2221 and <= 2720);
        if (!common) return false;
        var sum = 0;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            var digit = value[i] - '0';
            if ((value.Length - 1 - i) % 2 == 1) { digit *= 2; if (digit > 9) digit -= 9; }
            sum += digit;
        }
        return sum % 10 == 0;
    }
}
