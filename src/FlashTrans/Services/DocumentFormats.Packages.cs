using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Resolvers;

namespace FlashTrans.Services;

internal static partial class DocumentFormats
{
    sealed record PackageEntry(string Name, byte[] Bytes, DateTimeOffset Time);
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace StrictW = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    static void LoadPackage(TranslationDocument doc, byte[] data, CancellationToken ct)
    {
        var entries = new Dictionary<string, PackageEntry>(StringComparer.Ordinal);
        var documents = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        using (var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
        {
            if (archive.Entries.Count > 10000) throw new InvalidDataException("压缩包条目过多，请拆分文档。");
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if ((total += entry.Length) > MaxExpandedBytes || entry.Length > MaxFileBytes)
                    throw new InvalidDataException("文档解压内容过大（上限 128 MB），已停止读取。");
                if (entries.ContainsKey(entry.FullName)) throw new InvalidDataException("压缩包存在同名条目，无法安全回填。");
                using var stream = entry.Open();
                var bytes = new byte[checked((int)entry.Length)];
                stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1) throw new InvalidDataException("压缩包条目长度不一致，文件可能已损坏。");
                entries.Add(entry.FullName, new(entry.FullName, bytes, entry.LastWriteTime));
            }
        }
        if (entries.Keys.Any(n => n.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase) || n == "META-INF/signatures.xml"))
            throw new NotSupportedException("文档带数字签名，翻译会使签名失效；请使用未签名副本。");

        XDocument Read(string name, bool xhtml = false)
        {
            ct.ThrowIfCancellationRequested();
            if (documents.TryGetValue(name, out var cached)) return cached;
            if (!entries.TryGetValue(name, out var entry)) throw new InvalidDataException("文档缺少内部文件：" + name);
            if (entry.Bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("单个 XML 文件超过 16 MB，请拆分文档。");
            var settings = new XmlReaderSettings
            {
                // EPUB 2 常用 XHTML DTD。只解析框架内置 DTD，不访问网络或本机外部实体。
                DtdProcessing = xhtml ? DtdProcessing.Parse : DtdProcessing.Prohibit,
                XmlResolver = xhtml ? new XmlPreloadedResolver(null, XmlKnownDtds.Xhtml10) : null,
                MaxCharactersInDocument = 16 * 1024 * 1024,
                MaxCharactersFromEntities = 100000,
            };
            try
            {
                using var reader = XmlReader.Create(new MemoryStream(entry.Bytes), settings);
                var xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                documents.Add(name, xml);
                return xml;
            }
            catch (XmlException ex) { throw new InvalidDataException($"{name} 的 XML/XHTML 无法安全解析，请先用文档编辑器修复或重新另存。", ex); }
        }

        var epub = Path.GetExtension(doc.SourcePath).Equals(".epub", StringComparison.OrdinalIgnoreCase);
        if (epub) LoadEpub(doc, entries, Read, ct);
        else LoadDocx(doc, entries, Read, ct);

        doc.Write = stream =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            // EPUB 规范要求 mimetype 排在第一且不压缩；其余未修改资源按原字节复制。
            foreach (var entry in entries.Values.OrderBy(e => epub && e.Name == "mimetype" ? 0 : 1))
            {
                var output = archive.CreateEntry(entry.Name, epub && entry.Name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                output.LastWriteTime = entry.Time;
                using var target = output.Open();
                if (documents.TryGetValue(entry.Name, out var xml))
                {
                    using var writer = XmlWriter.Create(target, new XmlWriterSettings { Encoding = Utf8, Indent = false, NewLineHandling = NewLineHandling.None });
                    xml.Save(writer);
                }
                else target.Write(entry.Bytes);
            }
        };
    }

    static void LoadDocx(TranslationDocument doc, Dictionary<string, PackageEntry> entries,
        Func<string, bool, XDocument> read, CancellationToken ct)
    {
        if (!entries.ContainsKey("[Content_Types].xml")) throw new InvalidDataException("不是有效的 DOCX 文档。");
        var main = read("word/document.xml", false);
        var ns = main.Root?.Name.Namespace;
        if (ns != W && ns != StrictW) throw new InvalidDataException("无法识别 Word 文档命名空间。");
        var contentNames = entries.Keys.Where(n => n == "word/document.xml" || n.StartsWith("word/header", StringComparison.Ordinal)
            || n.StartsWith("word/footer", StringComparison.Ordinal) || n is "word/footnotes.xml" or "word/endnotes.xml" or "word/comments.xml")
            .Where(n => n.EndsWith(".xml", StringComparison.Ordinal)).ToArray();
        foreach (var name in contentNames)
        {
            ct.ThrowIfCancellationRequested();
            var xml = read(name, false);
            foreach (var paragraph in xml.Descendants(ns + "p"))
            {
                var texts = paragraph.Descendants(ns + "t")
                    .Where(t => t.Ancestors(ns + "p").First() == paragraph && !t.Ancestors(ns + "del").Any()).ToList();
                AddGroup(doc, texts.Select(t => new DocumentPart(t.Value, value =>
                {
                    t.Value = value;
                    t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
                })));
            }
        }
        doc.Warnings.Add("DOCX 翻译正文、表格、页眉页脚、脚注尾注、批注及 Word 文本框；保留样式、图片和链接。图片文字、公式、图表/SmartArt、嵌入对象及删除修订不翻译。译文可能改变分页；自动域更新可能覆盖字段结果。");
    }

    static void LoadEpub(TranslationDocument doc, Dictionary<string, PackageEntry> entries,
        Func<string, bool, XDocument> read, CancellationToken ct)
    {
        if (!entries.TryGetValue("mimetype", out var mime) || Encoding.ASCII.GetString(mime.Bytes).Trim() != "application/epub+zip")
            throw new InvalidDataException("不是有效的 EPUB 文件（缺少正确 mimetype）。");
        if (entries.ContainsKey("META-INF/encryption.xml"))
        {
            var encryption = read("META-INF/encryption.xml", false);
            var methods = encryption.Descendants().Where(e => e.Name.LocalName == "EncryptionMethod").ToList();
            if (methods.Count == 0 || methods.Any(e => (string?)e.Attribute("Algorithm") is not
                ("http://www.idpf.org/2008/embedding" or "http://ns.adobe.com/pdf/enc#RC")))
                throw new NotSupportedException("EPUB 含 DRM 或不支持的加密内容，不能翻译；仅允许保留已混淆的字体资源。");
        }
        var container = read("META-INF/container.xml", false);
        var roots = container.Descendants().Where(e => e.Name.LocalName == "rootfile").ToArray();
        if (roots.Length != 1) throw new NotSupportedException("暂不支持包含多个版本内容的 EPUB，请先导出单一版本。");
        var packagePath = (string?)roots[0].Attribute("full-path") ?? throw new InvalidDataException("EPUB 缺少 OPF 路径。");
        var package = read(packagePath, false);
        XNamespace opf = "http://www.idpf.org/2007/opf";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        var manifest = package.Descendants(opf + "item").ToArray();
        var ids = manifest.ToDictionary(e => (string?)e.Attribute("id") ?? "", StringComparer.Ordinal);
        var ordered = new List<XElement>();
        foreach (var itemref in package.Descendants(opf + "itemref"))
        {
            if (!ids.TryGetValue((string?)itemref.Attribute("idref") ?? "", out var item)) throw new InvalidDataException("EPUB 阅读顺序引用了不存在的章节。");
            ordered.Add(item);
        }
        ordered.AddRange(manifest);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var htmlRoots = new List<XElement>();
        foreach (var item in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var media = (string?)item.Attribute("media-type");
            if (media is not ("application/xhtml+xml" or "text/html" or "application/x-dtbncx+xml")) continue;
            var name = ResolvePackagePath(packagePath, (string?)item.Attribute("href") ?? "");
            if (!visited.Add(name)) continue;
            var xml = read(name, true);
            if (media == "application/x-dtbncx+xml")
            {
                foreach (var element in xml.Descendants().Where(e => e.Name.LocalName == "text"))
                    AddGroup(doc, [new(element.Value, value => element.Value = value)]);
            }
            else
            {
                if (xml.Root?.Name != Xhtml + "html") throw new InvalidDataException("EPUB 章节不是标准 XHTML：" + name);
                htmlRoots.Add(xml.Root);
                var texts = xml.DescendantNodes().OfType<XText>().Where(t => !BlockedXhtml(t) && NeedsTranslation(t.Value));
                // 同一段落的行内样式一起翻译，回填时不改动元素、id、href、CSS 或媒体资源。
                foreach (var group in texts.GroupBy(t => t.Ancestors().FirstOrDefault(IsXhtmlBlock) ?? t.Parent!))
                    AddGroup(doc, group.Select(t => new DocumentPart(t.Value, value => t.Value = value)));
            }
        }
        foreach (var element in package.Descendants().Where(e => e.Name == dc + "title" || e.Name == dc + "description" || e.Name == dc + "subject"))
            AddGroup(doc, [new(element.Value, value => element.Value = value)]);
        doc.Finish = target =>
        {
            var languages = package.Descendants(dc + "language").ToList();
            foreach (var language in languages) language.Value = target;
            if (languages.Count == 0) package.Root?.Element(opf + "metadata")?.Add(new XElement(dc + "language", target));
            foreach (var html in htmlRoots)
            {
                html.SetAttributeValue("lang", target);
                html.SetAttributeValue(XNamespace.Xml + "lang", target);
            }
        };
        doc.Warnings.Add("EPUB 翻译 XHTML 正文、导航目录及书名/简介/主题；保留章节顺序、图片、CSS、链接和字体，更新书籍与章节语言。代码、公式、SVG/图片文字、属性文字及标为不翻译的区域保留原文；不支持 DRM。");
    }

    static bool IsXhtmlBlock(XElement e) => e.Name.Namespace == Xhtml && e.Name.LocalName is
        "p" or "div" or "li" or "td" or "th" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "title" or "caption" or "figcaption";

    static bool BlockedXhtml(XText text) => text.Ancestors().Any(e => e.Name.Namespace != Xhtml ||
        e.Name.LocalName is "script" or "style" or "pre" or "code" or "kbd" or "samp" ||
        (string?)e.Attribute("translate") == "no" || ((string?)e.Attribute("class"))?.Split(' ').Contains("notranslate") == true);

    static string ResolvePackagePath(string parent, string href)
    {
        if (string.IsNullOrWhiteSpace(href) || href.Contains('\\') || Uri.TryCreate(href, UriKind.Absolute, out _))
            throw new InvalidDataException("EPUB 章节必须是包内相对路径。");
        var parts = new List<string>();
        var folder = parent.Contains('/') ? parent[..(parent.LastIndexOf('/') + 1)] : "";
        foreach (var part in (folder + Uri.UnescapeDataString(href.Split('#')[0])).Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new InvalidDataException("EPUB 路径越过包根目录。");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }
}
