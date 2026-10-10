using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FlashTrans.Services;

public sealed record GlossaryEntry(string Source, string Target);

/// <summary>只在用户选择的语向中应用术语；用占位符保护精确译法，不对整段译文做危险的全局替换。</summary>
public sealed class DocumentGlossary
{
    public static DocumentGlossary Empty { get; } = new([]);
    public IReadOnlyList<GlossaryEntry> Entries { get; }
    public List<string> Warnings { get; } = [];
    public string Fingerprint { get; }
    readonly Regex? _matcher;
    readonly Dictionary<string, string> _terms;

    public DocumentGlossary(IEnumerable<GlossaryEntry> entries)
    {
        _terms = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var source = entry.Source.Trim(); var target = entry.Target.Trim();
            if (source.Length is 0 or > 200 || target.Length is 0 or > 500 ||
                source.Any(char.IsControl) || target.Any(char.IsControl) || source.Contains('[') || target.Contains('['))
                throw new InvalidDataException("术语不能为空、包含控制字符或方括号；原词最长 200 字、译词最长 500 字。");
            XmlConvert.VerifyXmlChars(source); XmlConvert.VerifyXmlChars(target);
            if (_terms.TryGetValue(source, out var previous) && previous != target)
                throw new InvalidDataException("术语存在冲突译法：" + source);
            _terms[source] = target;
            if (_terms.Count > 100000) throw new InvalidDataException("术语超过 10 万条，请按领域拆分。");
        }
        Entries = _terms.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new GlossaryEntry(p.Key, p.Value)).ToArray();
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToTsv())));
        if (_terms.Count > 0)
        {
            // 优先长词；英文词边界不误改 identifier，中文短语按连续文字匹配。
            var alternatives = _terms.Keys.OrderByDescending(t => t.Length).Select(t =>
                (char.IsAsciiLetterOrDigit(t[0]) ? @"(?<![A-Za-z0-9_])" : "") + Regex.Escape(t) +
                (char.IsAsciiLetterOrDigit(t[^1]) ? @"(?![A-Za-z0-9_])" : ""));
            _matcher = new Regex(string.Join("|", alternatives), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
        }
    }

    public string ToTsv()
    {
        static string Quote(string value) => value.Contains('"') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        return "source\ttarget\n" + string.Join("\n", Entries.Select(e => Quote(e.Source) + "\t" + Quote(e.Target)));
    }

    public static DocumentGlossary Load(string path, string from, string target)
    {
        if (string.IsNullOrWhiteSpace(path)) return Empty;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 64 * 1024 * 1024) throw new InvalidDataException("术语文件超过 64 MB，请拆分后导入。");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), true);
        var text = reader.ReadToEnd();
        if (Path.GetExtension(path).Equals(".tbx", StringComparison.OrdinalIgnoreCase))
            return LoadTbx(text, from, target);
        return ParseDelimited(text, Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? ',' : '\t');
    }

    public static DocumentGlossary ParseDelimited(string text, char separator = '\t')
    {
        var entries = new List<GlossaryEntry>();
        var fields = new List<string>(); var field = new StringBuilder(); var quoted = false;
        void Row()
        {
            fields.Add(field.ToString()); field.Clear();
            if (fields.Any(f => !string.IsNullOrWhiteSpace(f)))
            {
                if (fields.Count != 2) throw new InvalidDataException("术语表必须是两列：原词、译词（CSV 或 Tab 分隔）。");
                if (!(entries.Count == 0 && fields[0].Trim().Equals("source", StringComparison.OrdinalIgnoreCase)
                    && fields[1].Trim().Equals("target", StringComparison.OrdinalIgnoreCase))) entries.Add(new(fields[0], fields[1]));
            }
            fields.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (quoted || field.Length == 0) quoted = !quoted;
                else throw new InvalidDataException("CSV 引号必须位于字段开头。");
            }
            else if (!quoted && c == separator) { fields.Add(field.ToString()); field.Clear(); }
            else if (!quoted && c is '\r' or '\n') { Row(); if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else field.Append(c);
        }
        if (quoted) throw new InvalidDataException("CSV 引号未闭合。");
        if (field.Length > 0 || fields.Count > 0) Row();
        return new(entries);
    }

    static DocumentGlossary LoadTbx(string text, string from, string target)
    {
        if (from == "auto") throw new InvalidDataException("导入 TBX 时请明确选择源语言，以免使用错误语向。");
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        // 公开 TBX 2 常引用外部 DTD；只忽略声明，不解析实体，也不访问网络。
        { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        var xml = XDocument.Load(reader);
        var entries = new List<GlossaryEntry>();
        foreach (var concept in xml.Descendants().Where(e => e.Name.LocalName is "termEntry" or "conceptEntry"))
        {
            var languages = concept.Elements().Where(e => e.Name.LocalName is "langSet" or "langSec").ToArray();
            var sources = languages.Where(e => SameLanguage((string?)e.Attribute(XNamespace.Xml + "lang"), from))
                .SelectMany(e => e.Descendants().Where(t => t.Name.LocalName == "term").Select(t => t.Value.Trim())).ToArray();
            var targets = languages.Where(e => SameLanguage((string?)e.Attribute(XNamespace.Xml + "lang"), target))
                .SelectMany(e => e.Descendants().Where(t => t.Name.LocalName == "term").Select(t => t.Value.Trim())).ToArray();
            if (targets.Length > 0) foreach (var source in sources) entries.Add(new(source, targets[0]));
        }
        if (entries.Count == 0) throw new InvalidDataException("TBX 中没有匹配当前源语言和目标语言的术语。");
        var groups = entries.GroupBy(e => e.Source, StringComparer.OrdinalIgnoreCase).ToArray();
        var unambiguous = groups.Where(g => g.Select(e => e.Target).Distinct(StringComparer.Ordinal).Count() == 1).ToArray();
        var glossary = new DocumentGlossary(unambiguous.Select(g => g.First()));
        if (groups.Length != unambiguous.Length)
            glossary.Warnings.Add($"已跳过 {groups.Length - unambiguous.Length} 个存在多义冲突的原词；需要时可在本地 TSV 中明确指定译法。");
        return glossary;
    }

    static bool SameLanguage(string? a, string b)
    {
        static string Normalize(string value) => value.Replace('_', '-').ToLowerInvariant() switch
        { "zh-cn" or "zh-sg" or "zh-hans" => "zh-hans", "zh-tw" or "zh-hk" or "zh-hant" => "zh-hant", var s => s };
        var left = Normalize(a ?? ""); var right = Normalize(b);
        return left == right || (!left.StartsWith("zh") && !right.StartsWith("zh") &&
            (!left.Contains('-') || !right.Contains('-')) && left.Split('-')[0] == right.Split('-')[0]);
    }

    internal (string Input, Func<string, string> Restore) Protect(DocumentUnit unit)
    {
        if (_matcher is null) return (unit.Input, text => text);
        var tag = "GT" + Guid.NewGuid().ToString("N")[..8];
        var values = new List<string>();
        var input = unit.BuildInput(text => _matcher.Replace(text, match =>
        {
            // Regex 的 Unicode 大小写折叠比 OrdinalIgnoreCase 宽，不因此误配相似字符。
            if (!_terms.TryGetValue(match.Value, out var translated)) return match.Value;
            var marker = $"[{tag}_{values.Count}]"; values.Add(translated); return marker;
        }));
        return (input, text =>
        {
            for (var i = 0; i < values.Count; i++)
            {
                var marker = $"[{tag}_{i}]";
                var at = text.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0 || at != text.LastIndexOf(marker, StringComparison.Ordinal))
                    throw new InvalidDataException("翻译源未保留术语标记；请使用 AI 源，不会输出术语失效的文件。");
                text = text.Replace(marker, values[i], StringComparison.Ordinal);
            }
            if (text.Contains("[" + tag, StringComparison.Ordinal)) throw new InvalidDataException("译文包含未知术语标记。");
            return text;
        });
    }
}
