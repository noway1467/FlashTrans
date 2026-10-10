using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using PdfSharp.Drawing;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace FlashTrans.Services;

internal static partial class DocumentFormats
{
    static void LoadPdf(TranslationDocument doc, byte[] data, CancellationToken ct)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(data);
        if (pdf.Structure.Trailer.EncryptionToken is not null)
            throw new NotSupportedException("暂不支持加密 PDF，请使用有权处理的未加密副本。");
        if (pdf.NumberOfPages > 500) throw new InvalidDataException("PDF 超过 500 页，请拆分后翻译。");
        var pages = new List<List<string>>();
        for (var n = 1; n <= pdf.NumberOfPages; n++)
        {
            ct.ThrowIfCancellationRequested();
            var page = pdf.GetPage(n);
            if (page.Letters.Count > 100000) throw new InvalidDataException("PDF 单页文字过多，已停止解析。");
            var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToArray();
            if (words.Length == 0)
                throw new NotSupportedException($"PDF 第 {n} 页没有可提取文本，可能是扫描件。当前不支持扫描件或混合扫描页，未忽略该页。");
            var blocks = RecursiveXYCut.Instance.GetBlocks(words);
            if (blocks.Count > 2000) throw new InvalidDataException("PDF 单页布局过于复杂，请先转换为文本或 DOCX。");
            // PDF 经常先绘制页脚再绘制正文；阅读顺序不能跟着绘制时间走。
            var ordered = new UnsupervisedReadingOrderDetector(5,
                UnsupervisedReadingOrderDetector.SpatialReasoningRules.ColumnWise, false).Get(blocks);
            var paragraphs = new List<string>();
            foreach (var block in ordered)
            {
                var original = block.Text.Replace("\r", "").Replace("\n", " ").Trim();
                if (original.Length == 0) continue;
                if (original.Contains('\uFFFD') || original.Contains('\0'))
                    throw new InvalidDataException($"PDF 第 {n} 页字符映射异常，请先检查原文件或使用 OCR。");
                var index = paragraphs.Count; paragraphs.Add(original);
                AddGroup(doc, [new(original, value => paragraphs[index] = value)]);
            }
            pages.Add(paragraphs);
            if (doc.Count > 20000 || doc.CharacterCount > 2_000_000) throw new InvalidDataException("PDF 正文超过处理上限，请拆分文件。");
        }
        doc.Warnings.Add("PDF 在本机提取文本层，生成 A4 正文重排版 PDF；不保留原图片、表格、公式与版式。多栏阅读顺序需人工核对。扫描件/无文本页不处理，未上传原文件。双语模式另存 HTML 阅读副本。");
        doc.Write = stream => WritePdfText(stream, pages);
    }

    internal static void WritePdfText(Stream stream, IReadOnlyList<List<string>> pages)
    {
        // WPF 构建通过 Windows 字体解析并嵌入子集，不依赖网络下载字体。
        var runes = pages.SelectMany(p => p).SelectMany(p => p.EnumerateRunes()).Where(r => !Rune.IsWhiteSpace(r)).Distinct().ToArray();
        XFont? font = null;
        foreach (var family in new[] { "Microsoft YaHei", "SimHei", "Segoe UI", "Arial" })
        {
            var typeface = new Typeface(family);
            if (!typeface.TryGetGlyphTypeface(out var glyphs) ||
                runes.Any(r => !glyphs.CharacterToGlyphMap.TryGetValue(r.Value, out var glyph) || glyph == 0)) continue;
            try { font = new XFont(typeface, 11, new XPdfFontOptions(PdfSharp.Pdf.PdfFontEncoding.Unicode)); break; }
            catch (InvalidOperationException) { /* 某些 Windows TTC 不能由 PDFsharp 嵌入，尝试覆盖全部字符的本机 TTF。 */ }
        }
        if (font is null) throw new NotSupportedException("没有能覆盖正文且可嵌入的系统字体，请安装对应语言字体，或选择双语 HTML 输出。");
        foreach (var paragraph in pages.SelectMany(p => p))
        foreach (var rune in paragraph.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            if (rune.Value is >= 0x0590 and <= 0x0FFF)
                throw new NotSupportedException("当前 PDF 字体或排版不支持部分字符，请改用双语 HTML 输出以保留文字。");
        }
        using var output = new PdfSharp.Pdf.PdfDocument();
        output.Info.Title = "FlashTrans · 正文译文";
        var labelFont = font;
        XGraphics? graphics = null;
        double y = 0;
        const double left = 48, width = 499, bottom = 787, lineHeight = 19;
        var sourcePage = 0;
        void NewPage()
        {
            graphics?.Dispose();
            var page = output.AddPage(); page.Size = PdfSharp.PageSize.A4;
            graphics = XGraphics.FromPdfPage(page);
            graphics.DrawString($"FlashTrans / Source page {sourcePage} / Reflowed text", labelFont, XBrushes.DimGray,
                new XRect(left, 24, width, 16), XStringFormats.TopLeft);
            graphics.DrawString(output.PageCount.ToString(), labelFont, XBrushes.DimGray,
                new XRect(left, 807, width, 16), XStringFormats.TopRight);
            y = 57;
        }
        void Line(string text)
        {
            if (y + lineHeight > bottom) NewPage();
            graphics!.DrawString(text, font, XBrushes.Black, new XRect(left, y, width, lineHeight), XStringFormats.TopLeft);
            y += lineHeight;
        }
        try
        {
            foreach (var paragraphs in pages)
            {
                sourcePage++; NewPage();
                foreach (var paragraph in paragraphs)
                {
                    var line = "";
                    var elements = StringInfo.GetTextElementEnumerator(paragraph);
                    while (elements.MoveNext())
                    {
                        var element = elements.GetTextElement();
                        if (line.Length > 0 && graphics!.MeasureString(line + element, font).Width > width)
                        {
                            // 尽量整词换行，长网址/代码仍按 Unicode 文本元素安全折行。
                            var split = line.LastIndexOf(' ');
                            if (split > line.Length / 2) { Line(line[..split]); line = line[(split + 1)..]; }
                            else { Line(line); line = ""; }
                        }
                        line += element;
                    }
                    if (line.Length > 0) Line(line);
                    y += 10;
                }
            }
        }
        finally { graphics?.Dispose(); }
        if (output.PageCount == 0) throw new InvalidDataException("PDF 没有正文。");
        output.Save(stream, closeStream: false);
    }
}
