using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FlashTrans.Services;
using PdfSharp.Drawing;

namespace FlashTrans.SelfTest;

static partial class DocumentProbe
{
    internal static void FeaturePreview(string directory)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try { FeaturePreviewOnDispatcher(directory); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    static void FeaturePreviewOnDispatcher(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!directory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("预览仅允许写入系统临时目录");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "sample.pdf");
        using (var stream = File.Create(source)) DocumentFormats.WritePdfText(stream,
            [["Hello world", "Hello world. " + string.Join(" ", Enumerable.Repeat("This is a local PDF translation example with preserved text and safe line wrapping.", 18))]]);
        var doc = Translate(source);
        doc.SaveAs(Path.Combine(directory, "translated.pdf"));
        doc.SaveAs(Path.Combine(directory, "bilingual.html"), bilingual: true);
        foreach (var theme in new[] { FlashTrans.Core.AppTheme.Dark, FlashTrans.Core.AppTheme.Light })
        {
            ThemeService.ApplyTheme(theme);
            var window = new FlashTrans.Views.DocumentTranslationWindow(new DocumentHistoryService(directory));
            try
            {
                window.Show(); PumpUntil(window.LoadFileAsync(source));
                Render(window, Path.Combine(directory, "documents-" + theme + ".png"));
            }
            finally { window.Close(); }
            var editor = new FlashTrans.Views.GlossaryWindow(new DocumentGlossary([new("translation", "翻译"), new("clipboard", "剪贴板")]), "");
            try { editor.Show(); editor.UpdateLayout(); Render(editor, Path.Combine(directory, "glossary-" + theme + ".png")); }
            finally { editor.Close(); }
        }
    }

    static void RunFeatureProbes(Action<string, Action> step)
    {
        step("文件：真实跨进程取消与加密断点恢复", () => InTemp(CheckpointProcesses));
        step("文件：原件/语向/源/术语/分批变更不误复用进度", () => InTemp(CheckpointIdentity));
        step("文件：断电尾记录恢复，中间损坏拒绝回填", () => InTemp(CheckpointCorruption));
        step("术语：CSV/TSV/TBX、语向、冲突及 XML 外部实体拒绝", () => InTemp(GlossaryFormats));
        step("术语：长词、边界、重复词与格式标记保护", () => InTemp(GlossaryTranslation));
        step("文件：TXT/Markdown/DOCX/EPUB 双语 HTML 与安全转义", () => InTemp(BilingualFiles));
        step("文件：双语输出可打开但不混入输入格式，危险扩展名拒绝", () =>
        {
            Check(FlashTrans.Views.DocumentTranslationWindow.IsOpenableResultPath("result.html") && !DocumentTranslation.Supports("result.html"), "双语输出/输入格式混淆");
            foreach (var extension in new[] { ".exe", ".cmd", ".lnk" })
                Check(!FlashTrans.Views.DocumentTranslationWindow.IsOpenableResultPath("file" + extension), "打开结果接受了危险扩展名");
        });
        step("PDF：真实文本层解析、中文重排、跨页及后台线程输出", () => InTemp(PdfRoundtrip));
        step("PDF：无文本、混合扫描页、加密与不支持字形明确拒绝", () => InTemp(PdfRejections));
    }

    internal static int CheckpointChild(string[] args)
    {
        try
        {
            var index = Array.IndexOf(args, "--checkpoint-child");
            var root = Path.GetFullPath(args[index + 1]); var mode = args[index + 2];
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("FlashTrans-doc-test-", StringComparison.Ordinal)) throw new InvalidOperationException("子测试目录越界");
            var doc = DocumentTranslation.Load(Path.Combine(root, "resume.txt"));
            var store = new DocumentCheckpointStore(root); var translator = new TestTranslator();
            using var cancel = new CancellationTokenSource();
            if (mode == "cancel") translator.OnCall = n => { if (n == 2) cancel.Cancel(); };
            try { DocumentTranslation.RunAsync(doc, translator, "en", "zh-CN", "child", null, cancel.Token, checkpoints: store).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (mode == "cancel") { }
            if (mode == "cancel") Check(doc.Completed == 1, "取消时未保留已完成段落");
            else
            {
                Check(doc.IsComplete && translator.Inputs.Count == 2, "新进程没有跳过已完成段落");
                doc.SaveAs(Path.Combine(root, "child-result.txt"));
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void CheckpointProcesses(string root)
    {
        PutText(root, "resume.txt", "Hello one\nHello two\nHello three");
        foreach (var mode in new[] { "cancel", "resume" })
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(DocumentProbe).Assembly.Location);
            start.ArgumentList.Add("--checkpoint-child"); start.ArgumentList.Add(root); start.ArgumentList.Add(mode);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("子测试超时"); }
            Check(process.ExitCode == 0, "子测试失败：" + errors.GetAwaiter().GetResult());
        }
        Check(File.ReadAllText(Path.Combine(root, "child-result.txt")).Contains("你好 three"), "恢复后译文不完整");
        var checkpoint = Directory.GetFiles(Path.Combine(root, "document-checkpoints")).Single();
        Check(File.ReadAllLines(checkpoint).All(line => line.StartsWith("dpapi:") && !line.Contains("Hello")), "进度未加密");
    }

    static void CheckpointIdentity(string root)
    {
        var path = PutText(root, "identity.txt", "Hello one\nHello two");
        var store = new DocumentCheckpointStore(root);
        void Run(string from, string to, string identity, int batch, DocumentGlossary? glossary, int expected)
        {
            var doc = DocumentTranslation.Load(path, batch); var t = new TestTranslator();
            DocumentTranslation.RunAsync(doc, t, from, to, identity, null, default, glossary: glossary, checkpoints: store).GetAwaiter().GetResult();
            Check(t.Inputs.Count == expected && doc.IsComplete, "进度身份校验错误：" + identity);
        }
        Run("en", "zh-CN", "a", 600, null, 2); Run("en", "zh-CN", "a", 600, null, 0);
        Run("en", "ja", "a", 600, null, 2); Run("en", "zh-CN", "b", 600, null, 2);
        Run("en", "zh-CN", "a", 250, null, 2);
        Run("en", "zh-CN", "a", 600, new DocumentGlossary([new("one", "第一")]), 2);
        File.AppendAllText(path, "\nHello four"); Run("en", "zh-CN", "a", 600, null, 3);
        Check(store.Clear() == 6 && File.Exists(path), "清理进度数量错误或删掉了原件");
    }

    static void CheckpointCorruption(string root)
    {
        var path = PutText(root, "broken.txt", "Hello one\nHello two\nHello three");
        var store = new DocumentCheckpointStore(root);
        var doc = DocumentTranslation.Load(path);
        DocumentTranslation.RunAsync(doc, new TestTranslator(), "en", "zh-CN", "a", null, default, checkpoints: store).GetAwaiter().GetResult();
        var checkpoint = Directory.GetFiles(Path.Combine(root, "document-checkpoints")).Single();
        var lines = File.ReadAllLines(checkpoint);
        File.WriteAllText(checkpoint, lines[0] + "\n" + lines[1] + "\n" + lines[2][..20], Utf8);
        doc = DocumentTranslation.Load(path); var translator = new TestTranslator();
        DocumentTranslation.RunAsync(doc, translator, "en", "zh-CN", "a", null, default, checkpoints: store).GetAwaiter().GetResult();
        Check(doc.IsComplete && translator.Inputs.Count == 1 && doc.CheckpointWarning is not null, "尾部截断没有安全恢复");
        File.WriteAllText(checkpoint, lines[0] + "\ndpapi:broken\n", Utf8);
        doc = DocumentTranslation.Load(path);
        Throws<InvalidDataException>(() => DocumentTranslation.RunAsync(doc, new TestTranslator(), "en", "zh-CN", "a", null, default, checkpoints: store).GetAwaiter().GetResult());
        Check(doc.Completed == 0, "损坏进度发生部分回填");
    }

    static void GlossaryFormats(string root)
    {
        var csv = DocumentGlossary.ParseDelimited("source,target\r\n\"hello, world\",你好世界\r\n\"say \"\"hello\"\"\",问好", ',');
        Check(csv.Entries.Count == 2 && DocumentGlossary.ParseDelimited(csv.ToTsv()).Fingerprint == csv.Fingerprint, "CSV/TSV 引号往返失败");
        Throws<InvalidDataException>(() => DocumentGlossary.ParseDelimited("x\tone\nx\ttwo"));
        Throws<InvalidDataException>(() => DocumentGlossary.ParseDelimited("x\ty\tz"));
        var tbx = PutText(root, "terms.tbx", "<tbx xmlns='urn:iso:std:iso:30042:ed-2'><text><body><conceptEntry id='1'><langSec xml:lang='en-US'><termSec><term>computer</term></termSec></langSec><langSec xml:lang='zh-CN'><termSec><term>计算机</term></termSec></langSec><langSec xml:lang='zh-TW'><termSec><term>電腦</term></termSec></langSec></conceptEntry></body></text></tbx>");
        Check(DocumentGlossary.Load(tbx, "en", "zh-Hans").Entries.Single().Target == "计算机", "TBX 简体语向错误");
        Check(DocumentGlossary.Load(tbx, "en", "zh-TW").Entries.Single().Target == "電腦", "TBX 简繁混用");
        Throws<InvalidDataException>(() => DocumentGlossary.Load(tbx, "auto", "zh-CN"));
        var v2 = PutText(root, "terms-v2.tbx", "<!DOCTYPE martif SYSTEM 'https://example.invalid/never-download.dtd'><martif><text><body><termEntry id='1'><langSet xml:lang='en'><tig><term>clipboard</term></tig></langSet><langSet xml:lang='zh-CN'><tig><term>剪贴板</term></tig></langSet></termEntry></body></text></martif>");
        Check(DocumentGlossary.Load(v2, "en", "zh-CN").Entries.Single().Target == "剪贴板", "带 DTD 声明的 TBX 2 不兼容");
        File.WriteAllText(tbx, "<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///never-read'>]><tbx>&x;</tbx>");
        Throws<System.Xml.XmlException>(() => DocumentGlossary.Load(tbx, "en", "zh-CN"));
    }

    static void GlossaryTranslation(string root)
    {
        var path = PutText(root, "terms.txt", "Hello world, Hello world. world WORLD worldview world_id");
        var glossary = new DocumentGlossary([new("Hello world", "您好"), new("world", "地球")]);
        var doc = DocumentTranslation.Load(path); var t = new TestTranslator { Transform = text => text };
        DocumentTranslation.RunAsync(doc, t, "en", "zh-CN", "t", null, default, glossary: glossary).GetAwaiter().GetResult();
        var output = doc.SaveCopy(root, "zh-CN"); var result = File.ReadAllText(output);
        Check(result == "您好, 您好. 地球 地球 worldview world_id", "术语边界或长词优先错误：" + result);
        Check(t.Inputs.Single().Contains("[GT") && !result.Contains("[GT"), "术语占位符未保护/恢复");
        var broken = DocumentTranslation.Load(path);
        Throws<InvalidOperationException>(() => DocumentTranslation.RunAsync(broken,
            new TestTranslator { Transform = text => Regex.Replace(text, @"\[GT[^\]]+\]", "missing") },
            "en", "zh-CN", "bad", null, default, glossary: glossary).GetAwaiter().GetResult());
        Check(broken.Completed == 0, "术语标记损坏仍被接受");
    }

    static void BilingualFiles(string root)
    {
        var paths = new List<string>
        {
            PutText(root, "bilingual.txt", "Hello <script>alert(1)</script> world"),
            PutText(root, "bilingual.md", "Hello **world**\n\n```cs\nvar Hello = 1;\n```"),
        };
        var epub = Path.Combine(root, "bilingual.epub"); Package(epub, EpubEntries()); paths.Add(epub);
        // 复用已有 DOCX 结构样例，额外验证同一解析结果的第二种输出。
        DocxRoundtrip(root); paths.Add(Path.Combine(root, "input.docx"));
        foreach (var path in paths)
        {
            var before = File.ReadAllBytes(path); var doc = Translate(path);
            var output = doc.SaveCopy(root, "zh-CN", bilingual: true); var html = File.ReadAllText(output);
            Check(output.EndsWith(".html") && html.Contains("<section>") && html.Contains("Hello") && html.Contains("你好"), "双语内容缺失");
            Check(!html.Contains("<script>") && html.Contains("Content-Security-Policy"), "原文 HTML 未安全转义");
            Check(File.ReadAllBytes(path).SequenceEqual(before), "双语导出修改原件");
            Check(doc.SaveCopy(root, "zh-CN", bilingual: true) != output, "双语重名覆盖");
            var store = new DocumentCheckpointStore(root);
            var noRequests = new TestTranslator();
            DocumentTranslation.RunAsync(doc, noRequests, "en", "zh-CN", "test", null, default, checkpoints: store).GetAwaiter().GetResult();
            var reload = DocumentTranslation.Load(path);
            DocumentTranslation.RunAsync(reload, noRequests, "en", "zh-CN", "test", null, default, checkpoints: store).GetAwaiter().GetResult();
            Check(noRequests.Inputs.Count == 0 && reload.IsComplete, "补存进度或格式片段的恢复失败：" + Path.GetExtension(path));
        }
    }

    static void PdfRoundtrip(string root)
    {
        var source = Path.Combine(root, "sample.pdf");
        using (var stream = File.Create(source)) DocumentFormats.WritePdfText(stream,
            [["Hello world", string.Join(" ", Enumerable.Repeat("Hello long paragraph", 300))], ["Hello second page"]]);
        var doc = Translate(source); var output = doc.SaveCopy(root, "zh-CN");
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(output))
        {
            var text = string.Concat(pdf.GetPages().Select(p => p.Text));
            Check(pdf.NumberOfPages >= 2 && text.Contains("你好") && text.Contains("世界"), "PDF 中文或分页丢失");
        }
        var background = Path.Combine(root, "background.pdf");
        Task.Run(() => doc.SaveAs(background)).GetAwaiter().GetResult();
        Check(File.Exists(background), "后台线程 PDF 输出失败");
        Check(File.ReadAllText(doc.SaveCopy(root, "zh-CN", bilingual: true)).Contains("你好"), "PDF 双语输出失败");
    }

    static void PdfRejections(string root)
    {
        var path = Path.Combine(root, "empty.pdf");
        using (var pdf = new PdfSharp.Pdf.PdfDocument()) { pdf.AddPage(); pdf.Save(path); }
        Throws<NotSupportedException>(() => DocumentTranslation.Load(path));
        var mixed = Path.Combine(root, "mixed.pdf");
        using (var pdf = new PdfSharp.Pdf.PdfDocument())
        {
            using (var g = XGraphics.FromPdfPage(pdf.AddPage())) g.DrawString("Hello", new XFont("Arial", 14), XBrushes.Black, new XPoint(50, 50));
            pdf.AddPage(); pdf.Save(mixed);
        }
        Throws<NotSupportedException>(() => DocumentTranslation.Load(mixed));
        var encrypted = Path.Combine(root, "encrypted.pdf");
        using (var pdf = new PdfSharp.Pdf.PdfDocument())
        { pdf.AddPage(); pdf.SecuritySettings.OwnerPassword = "test"; pdf.Save(encrypted); }
        Throws<NotSupportedException>(() => DocumentTranslation.Load(encrypted));
        using var memory = new MemoryStream();
        Throws<NotSupportedException>(() => DocumentFormats.WritePdfText(memory, [["مرحبا"]]));
    }
}
