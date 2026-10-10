using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace FlashTrans.Services;

/// <summary>格式层只选择正文并回填；翻译器永远没有文件系统或文档结构的控制权。</summary>
internal static partial class DocumentFormats
{
    const int MaxFileBytes = 64 * 1024 * 1024;
    const int MaxExpandedBytes = 128 * 1024 * 1024;
    static readonly UTF8Encoding Utf8 = new(false, true);

    internal static TranslationDocument Load(string path, int batchCharacters, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        if (!DocumentTranslation.Supports(path)) throw new NotSupportedException("支持 EPUB、TXT、Markdown、DOCX 和文本层 PDF；旧版 .doc 请先另存为 .docx。");
        ct.ThrowIfCancellationRequested();
        // 打开后再核验长度，并限制读取；不因文件被替换或 ZIP 压缩比过高而耗尽内存。
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxFileBytes) throw new InvalidDataException("文件超过 64 MB，请拆分后翻译。");
        var data = new byte[checked((int)input.Length)];
        input.ReadExactly(data);
        var document = new TranslationDocument
        {
            SourcePath = path,
            SourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)),
            BatchCharacters = Math.Clamp(batchCharacters, DocumentTranslation.MinBatchCharacters, DocumentTranslation.MaxBatchCharacters)
        };
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".txt": LoadText(document, DecodeText(document, data), false, ct); break;
            case ".md": case ".markdown": LoadText(document, DecodeText(document, data), true, ct); break;
            case ".pdf": LoadPdf(document, data, ct); break;
            default: LoadPackage(document, data, ct); break;
        }
        if (document.Count > 20000 || document.CharacterCount > 2_000_000)
            throw new InvalidDataException("正文超过 200 万字符或 2 万个分段，请拆分文件。");
        ct.ThrowIfCancellationRequested();
        return document;
    }

    static string DecodeText(TranslationDocument doc, byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = Utf8;
        var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) { encoding = new UTF32Encoding(true, false, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) skip = 3;
        string text;
        try { text = encoding.GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException) when (skip == 0)
        {
            encoding = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            text = encoding.GetString(bytes);
            doc.Warnings.Add("输入不是有效 UTF-8，已按 GB18030（兼容 GBK）读取；编码自动识别有歧义，请核对原文。输出统一为 UTF-8。");
        }
        if (text.Contains('\0')) throw new InvalidDataException("文件含 NUL，可能是无 BOM 的 UTF-16 或二进制文件；请先另存为 UTF-8。");
        return text;
    }

    static void LoadText(TranslationDocument doc, string text, bool markdown, CancellationToken ct)
    {
        var replacements = new SortedDictionary<int, (int Length, string Text)>();
        if (!markdown)
        {
            foreach (Match line in Regex.Matches(text, @"[^\r\n]+"))
            {
                ct.ThrowIfCancellationRequested();
                var start = line.Index;
                var length = line.Length;
                AddGroup(doc, [new(line.Value, value => replacements[start] = (length, value))]);
            }
        }
        else
        {
            var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseYamlFrontMatter().UsePreciseSourceLocation().Build();
            var syntax = Markdown.Parse(text, pipeline);
            var anchors = syntax.Descendants<LinkInline>().Select(l => l.Url).Where(u => u?.StartsWith('#') == true)
                .Select(u => Uri.UnescapeDataString(u![1..])).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var leaf in syntax.Descendants<LeafBlock>())
            {
                ct.ThrowIfCancellationRequested();
                if (leaf.Inline is null) continue; // 代码块、HTML 块、YAML 等没有可翻译的 Inline 正文。
                // 标题文字常被渲染器用作锚点；保留被内部链接引用的标题，避免目录断链。
                if (leaf is HeadingBlock && leaf.TryGetAttributes()?.Id is { } id && anchors.Contains(id))
                {
                    if (!doc.Warnings.Contains("为保留内部目录链接，其指向的 Markdown 标题保留原文。"))
                        doc.Warnings.Add("为保留内部目录链接，其指向的 Markdown 标题保留原文。");
                    continue;
                }
                var parts = new List<DocumentPart>();
                foreach (var literal in leaf.Inline.Descendants<LiteralInline>())
                {
                    if (InsideProtectedLink(literal)) continue;
                    var start = literal.Span.Start;
                    var length = literal.Span.Length;
                    if (start < 0 || length <= 0 || start + length > text.Length)
                        throw new InvalidDataException("Markdown 正文位置无法可靠定位，已停止以保护原文件。");
                    // 表格扩展会 Trim Content，但 Span 仍包含两侧空格；必须从原始范围保留边界。
                    var raw = text.Substring(start, length);
                    var content = raw[..(raw.Length - raw.TrimStart().Length)] + literal.Content.ToString().Trim() + raw[raw.TrimEnd().Length..];
                    parts.Add(new(content, value => replacements[start] = (length, EscapeMarkdown(value))));
                }
                AddGroup(doc, parts);
            }
            doc.Warnings.Add("Markdown 保留代码、公式、链接地址、图片、HTML 块及 YAML 元数据；这些区域及链接 title/图片替代文字/引用式链接标签不翻译。正文按语法树回填。");
        }
        doc.Write = stream =>
        {
            using var writer = new StreamWriter(stream, Utf8, leaveOpen: true);
            var end = 0;
            foreach (var (start, replacement) in replacements)
            {
                if (start < end) throw new InvalidDataException("正文范围重叠，已停止导出。");
                writer.Write(text.AsSpan(end, start - end));
                writer.Write(replacement.Text);
                end = start + replacement.Length;
            }
            writer.Write(text.AsSpan(end));
        };
    }

    static bool InsideProtectedLink(Inline inline)
    {
        for (var parent = inline.Parent; parent is not null; parent = parent.Parent)
            if (parent is LinkInline link && (link.IsImage || link.Reference is not null || link.IsAutoLink)) return true;
        return false;
    }

    static string EscapeMarkdown(string text)
    {
        var result = new StringBuilder();
        foreach (var ch in text)
        {
            if ("\\`*_{}[]<>#!|~+-.=&".Contains(ch)) result.Append('\\');
            result.Append(ch);
        }
        return result.ToString();
    }

    static bool NeedsTranslation(string text) => text.EnumerateRunes().Any(Rune.IsLetter)
        && !Uri.TryCreate(text.Trim(), UriKind.Absolute, out _);

    static void AddGroup(TranslationDocument doc, IEnumerable<DocumentPart> source)
    {
        List<DocumentPart> pending = [];
        var size = 0;
        foreach (var original in source)
        {
            var chunks = Split(original.Text, doc.BatchCharacters).ToArray();
            var values = chunks.ToArray();
            for (var i = 0; i < chunks.Length; i++)
            {
                var raw = chunks[i];
                var trimmed = raw.Trim();
                if (!NeedsTranslation(trimmed)) continue;
                var prefix = raw[..(raw.Length - raw.TrimStart().Length)];
                var suffix = raw[raw.TrimEnd().Length..];
                var index = i;
                if (pending.Count > 0 && (size + trimmed.Length + 20 > doc.BatchCharacters || pending.Count >= 12))
                {
                    doc.Units.Add(new(pending)); pending = []; size = 0;
                }
                pending.Add(new(trimmed, translated =>
                {
                    values[index] = prefix + translated + suffix;
                    original.Set(string.Concat(values));
                }));
                size += trimmed.Length + 20;
            }
        }
        if (pending.Count > 0) doc.Units.Add(new(pending));
    }

    static IEnumerable<string> Split(string text, int maxChunk)
    {
        // 保留所有换行。长行优先按句子/空格切；极长单词按 Unicode 文本元素边界切。
        var start = 0;
        while (start < text.Length)
        {
            var count = Math.Min(maxChunk, text.Length - start);
            var newline = text.IndexOfAny(['\r', '\n'], start, count);
            if (newline >= 0) count = newline - start + 1;
            else if (start + count < text.Length)
            {
                var boundary = text.LastIndexOfAny([' ', '\t', '.', '。', '!', '?', '！', '？', ';', '；'], start + count - 1, count / 2);
                if (boundary >= start) count = boundary - start + 1;
                else
                {
                    var offsets = StringInfo.ParseCombiningCharacters(text.Substring(start, Math.Min(count + 16, text.Length - start)));
                    count = offsets.LastOrDefault(n => n <= count);
                    if (count == 0) throw new InvalidDataException("文本包含过长的 Unicode 组合字符，无法安全分段。");
                }
            }
            yield return text.Substring(start, count);
            start += count;
        }
    }
}
