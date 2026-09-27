using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using FlashTrans.Core;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

static class DocumentProbe
{
    static readonly UTF8Encoding Utf8 = new(false);
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace H = "http://www.w3.org/1999/xhtml";

    internal static void RunAll(Action<string, Action> step)
    {
        step("文件：TXT 编码、换行、长段落和另存保护", () => InTemp(TextRoundtrip));
        step("文件：Markdown 正文翻译，代码/链接/表格/元数据不损坏", () => InTemp(MarkdownRoundtrip));
        step("文件：DOCX 表格/行内样式/页眉/脚注及媒体保留", () => InTemp(DocxRoundtrip));
        step("文件：EPUB 阅读顺序、导航、XHTML DTD 和资源保留", () => InTemp(EpubRoundtrip));
        step("文件：格式标记损坏阻止导出", () => InTemp(InvalidMarkers));
        step("文件：取消、当前会话续译和换源重译", () => InTemp(CancelResume));
        step("文件：空正文、损坏包、DRM、重复条目及外部实体拒绝", () => InTemp(InvalidFiles));
        step("文件：本机兼容 HTTP 接口实际请求与超时取消", () => InTemp(LocalHttpRoundtrip));
        step("文件：WPF 深浅主题窗口、读取、按钮和渲染", () => InTemp(WindowRoundtrip));
        step("文件：自动副本、目录选择和同名避让", () => InTemp(AutomaticCopies));
        step("文件：历史持久化、上限、更新、清空及损坏保护", () => InTemp(HistoryPersistence));
        step("文件：默认输出配置迁移及往返，版本 1.9.1", () => InTemp(OutputSettings));
        step("文件：路径与超时文本的完整高度（字号、缩放、禁用状态）", () => InTemp(InputTextHeight));
        step("文件：目标语言独立持久化，重开、重载及历史恢复", () => InTemp(TargetLanguagePersistence));
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    static void InTemp(Action<string> test)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FlashTrans-doc-test-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try { test(root); }
        finally
        {
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("临时路径越界");
            Directory.Delete(root, recursive: true);
        }
    }
    static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException("应抛出 " + typeof(T).Name);
    }
    static TranslationDocument Translate(string path, TestTranslator? translator = null)
    {
        var doc = DocumentTranslation.Load(path);
        DocumentTranslation.RunAsync(doc, translator ?? new(), "en", "zh-CN", "test", null, default).GetAwaiter().GetResult();
        Check(doc.IsComplete, "完成状态错误");
        return doc;
    }
    static string PutText(string root, string name, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, text, encoding ?? Utf8);
        return path;
    }
    static void TextRoundtrip(string root)
    {
        var original = "  Hello world!\r\n\r\n\t12345\r\n" + string.Concat(Enumerable.Repeat("Hello 😀 world! ", 250)) + "\n";
        var path = PutText(root, "input.txt", original, Encoding.Unicode);
        var before = File.ReadAllBytes(path);
        var translator = new TestTranslator();
        var doc = Translate(path, translator);
        Check(translator.Inputs.All(t => t.Length <= 1800), "分块超限");
        Check(translator.Inputs.All(t => !t.Contains('\uFFFD')), "Unicode 被切坏");
        Throws<IOException>(() => doc.SaveAs(path));
        var output = Path.Combine(root, "output.txt");
        doc.SaveAs(output);
        Check(File.ReadAllText(output, Utf8) == original.Replace("Hello", "你好").Replace("world", "世界"), "TXT 内容或换行发生变化");
        Throws<IOException>(() => doc.SaveAs(output));
        Throws<IOException>(() => doc.SaveAs(Path.Combine(root, "output.docx")));
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "原文件被修改");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gb = PutText(root, "gb.txt", "你好世界", Encoding.GetEncoding("GB18030"));
        var gbDoc = DocumentTranslation.Load(gb);
        Check(gbDoc.Warnings.Any(w => w.Contains("GB18030")), "GBK 回退未提示");
        Check(gbDoc.Units[0].Input == "你好世界", "GBK 解码错误");
        var astral = DocumentTranslation.Load(PutText(root, "astral.txt", "\U00020000\U00020001"));
        Check(astral.CharacterCount == 4, "补充平面汉字被误当成非正文跳过");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Throws<OperationCanceledException>(() => doc.SaveAs(Path.Combine(root, "cancel.txt"), cancel.Token));
        Check(!Directory.EnumerateFiles(root, ".flashtrans-*").Any(), "保存临时文件未清理");
    }
    static void MarkdownRoundtrip(string root)
    {
        const string text = "---\ntitle: Hello\n---\n# Hello world\n\nHello **world** [Hello](https://example.com/a_(b) \"Hello\") and `Hello()` ![Hello](image.png)\n\n```cs\nHello();\n```\n\n    Hello indented code\n\n| Hello | world |\n| --- | --- |\n| Hello | `world` |\n\n- [x] Hello\n\n[ref]: https://example.com/Hello \"Hello\"\n\n<div>HTML Hello</div>\n\n$Hello + world$\n";
        var path = PutText(root, "input.md", text);
        var translator = new TestTranslator();
        var doc = Translate(path, translator);
        var output = Path.Combine(root, "out.md"); doc.SaveAs(output);
        var result = File.ReadAllText(output);
        foreach (var preserved in new[] { "title: Hello", "`Hello()`", "![Hello](image.png)", "```cs\nHello();\n```", "    Hello indented code", "[ref]: https://example.com/Hello \"Hello\"", "<div>HTML Hello</div>", "$Hello + world$", "| --- | --- |", "https://example.com/a_(b) \"Hello\"" })
            Check(result.Contains(preserved), "Markdown 保护失败：" + preserved);
        Check(result.Contains("# 你好 世界"), "标题没有翻译");
        Check(result.Contains("**世界**") && result.Contains("[你好](https://"), "行内样式或链接标签损坏");
        Check(result.Contains("| 你好 | 世界 |") && result.Contains("- [x] 你好"), "表格或任务列表损坏，实际：" + result);
        Check(translator.Inputs.All(t => !t.Contains("Hello()") && !t.Contains("https://")), "代码或 URL 被发给翻译源");
        var references = PutText(root, "references.markdown", "# Hello world\n\n[Hello world](#hello-world)\n\n[Hello]\n\n[Hello][]\n\n[Hello]: https://example.com\n");
        var refs = Translate(references); var refsOut = Path.Combine(root, "refs-out.markdown"); refs.SaveAs(refsOut);
        var refsText = File.ReadAllText(refsOut);
        Check(refsText.Contains("# Hello world") && refsText.Contains("[Hello][]") && refsText.Contains("\n[Hello]\n"), "Markdown 标题锚点或简写引用被破坏");
    }

    static void Package(string path, IEnumerable<(string Name, string Text)> entries, byte[]? media = null)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name, name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open(), Utf8);
            writer.Write(text);
        }
        if (media is not null)
        {
            using var stream = archive.CreateEntry("assets/test.bin").Open(); stream.Write(media);
        }
    }
    static byte[] Entry(string path, string name)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(name)!.Open();
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
    static void DocxRoundtrip(string root)
    {
        var path = Path.Combine(root, "input.docx");
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var body = $"<w:document xmlns:w='{W}' xmlns:r='{rel}'><w:body><w:p><w:pPr><w:pStyle w:val='Title'/></w:pPr><w:r><w:t xml:space='preserve'>Hello </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>world</w:t></w:r><w:r><w:footnoteReference w:id='1'/></w:r></w:p><w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w='4000'/></w:tblGrid><w:tr><w:tc><w:p><w:r><w:t>Hello</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p><w:del w:id='1' w:author='Test'><w:r><w:delText>Hello deleted</w:delText></w:r></w:del></w:p><w:sectPr><w:headerReference w:type='default' r:id='header'/></w:sectPr></w:body></w:document>";
        Package(path,
        [
            ("[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Default Extension='xml' ContentType='application/xml'/><Default Extension='bin' ContentType='application/octet-stream'/><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/><Override PartName='/word/header1.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml'/><Override PartName='/word/footnotes.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml'/><Override PartName='/word/styles.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml'/></Types>"),
            ("_rels/.rels", $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='document' Type='{rel}/officeDocument' Target='word/document.xml'/></Relationships>"),
            ("word/_rels/document.xml.rels", $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='header' Type='{rel}/header' Target='header1.xml'/><Relationship Id='footnotes' Type='{rel}/footnotes' Target='footnotes.xml'/><Relationship Id='styles' Type='{rel}/styles' Target='styles.xml'/></Relationships>"),
            ("word/document.xml", body),
            ("word/header1.xml", $"<w:hdr xmlns:w='{W}'><w:p><w:r><w:t>Hello header</w:t></w:r></w:p></w:hdr>"),
            ("word/footnotes.xml", $"<w:footnotes xmlns:w='{W}'><w:footnote w:id='1'><w:p><w:r><w:footnoteRef/></w:r><w:r><w:t>Hello footnote</w:t></w:r></w:p></w:footnote></w:footnotes>"),
            ("word/styles.xml", $"<w:styles xmlns:w='{W}'><w:style w:type='paragraph' w:styleId='Title'><w:name w:val='Title'/></w:style></w:styles>")
        ], [0, 1, 2, 255]);
        var before = File.ReadAllBytes(path);
        var doc = Translate(path); var output = Path.Combine(root, "out.docx"); doc.SaveAs(output);
        var xml = XDocument.Parse(Utf8.GetString(Entry(output, "word/document.xml")));
        Check(xml.Descendants(W + "t").Select(t => t.Value).SequenceEqual(new[] { "你好 ", "世界", "你好" }), "Word 正文/表格翻译不完整");
        Check(xml.Descendants(W + "b").Count() == 1 && xml.Descendants(W + "tbl").Count() == 1 && xml.Descendants(W + "pStyle").Single().Attribute(W + "val")!.Value == "Title", "Word 样式结构改变");
        Check(Utf8.GetString(Entry(output, "word/header1.xml")).Contains("你好 header"), "页眉未翻译");
        Check(Utf8.GetString(Entry(output, "word/footnotes.xml")).Contains("你好 footnote"), "脚注未翻译");
        foreach (var name in new[] { "assets/test.bin", "word/styles.xml", "[Content_Types].xml", "word/_rels/document.xml.rels", "_rels/.rels" }) Check(Entry(path, name).SequenceEqual(Entry(output, name)), "Word 资源改变：" + name);
        Check(File.ReadAllBytes(path).SequenceEqual(before), "Word 原件改变");
    }

    static (string Name, string Text)[] EpubEntries() =>
    [
        ("mimetype", "application/epub+zip"),
        ("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='OPS/book.opf'/></rootfiles></container>"),
        ("OPS/book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>Hello book</dc:title><dc:language>en</dc:language></metadata><manifest><item id='one' href='chapters/a.xhtml' media-type='application/xhtml+xml'/><item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/></manifest><spine><itemref idref='one'/></spine></package>"),
        ("OPS/chapters/a.xhtml", "<!DOCTYPE html PUBLIC '-//W3C//DTD XHTML 1.0 Strict//EN' 'http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd'><html xmlns='http://www.w3.org/1999/xhtml'><head><title>Hello title</title><style>.Hello{color:red}</style></head><body><p id='p1'>Hello <b>world</b>&nbsp;<a href='../nav.xhtml'>Hello</a></p><pre>Hello code</pre><p translate='no'>Hello protected</p><img src='../../assets/test.bin'/></body></html>"),
        ("OPS/nav.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Hello</title></head><body><nav><a href='chapters/a.xhtml#p1'>Hello chapter</a></nav></body></html>"),
        ("OPS/style.css", ".Hello{font-size:12px}")
    ];
    static void EpubRoundtrip(string root)
    {
        var path = Path.Combine(root, "input.epub"); Package(path, EpubEntries(), [1, 2, 3, 255]);
        var doc = Translate(path); var output = Path.Combine(root, "out.epub"); doc.SaveAs(output);
        var xml = XDocument.Parse(Utf8.GetString(Entry(output, "OPS/chapters/a.xhtml")));
        Check(xml.Descendants(H + "p").First().Value == "你好 世界\u00a0你好", "EPUB 正文不正确");
        Check(xml.Descendants(H + "pre").Single().Value == "Hello code", "EPUB 代码被翻译");
        Check(xml.Descendants(H + "p").Last().Value == "Hello protected", "EPUB translate=no 失效");
        Check(xml.Descendants(H + "a").Single().Attribute("href")!.Value == "../nav.xhtml", "EPUB 链接损坏");
        Check(Utf8.GetString(Entry(output, "OPS/nav.xhtml")).Contains("你好 chapter"), "EPUB 导航未翻译");
        Check(Utf8.GetString(Entry(output, "OPS/book.opf")).Contains(">zh-CN</dc:language>") && (string?)xml.Root!.Attribute("lang") == "zh-CN", "EPUB 语言信息未更新");
        foreach (var name in new[] { "assets/test.bin", "OPS/style.css", "mimetype" }) Check(Entry(path, name).SequenceEqual(Entry(output, name)), "EPUB 资源改变");
        var bytes = File.ReadAllBytes(output);
        Check(BitConverter.ToUInt16(bytes, 8) == 0 && Encoding.ASCII.GetString(bytes, 30, 8) == "mimetype", "EPUB mimetype 未置首或被压缩");
    }

    static void InvalidMarkers(string root)
    {
        var path = PutText(root, "input.md", "Hello **world**");
        var doc = DocumentTranslation.Load(path);
        Throws<InvalidOperationException>(() => DocumentTranslation.RunAsync(doc, new TestTranslator { Transform = _ => "丢失标记" }, "en", "zh-CN", "bad", null, default).GetAwaiter().GetResult());
        Check(doc.Completed == 0, "损坏译文被标为完成");
        Throws<InvalidOperationException>(() => doc.SaveAs(Path.Combine(root, "bad.md")));
    }
    static void CancelResume(string root)
    {
        var path = PutText(root, "input.txt", "Hello\nworld\nHello again");
        var doc = DocumentTranslation.Load(path);
        using var cts = new CancellationTokenSource();
        var fake = new TestTranslator { OnCall = n => { if (n == 2) cts.Cancel(); } };
        Throws<OperationCanceledException>(() => DocumentTranslation.RunAsync(doc, fake, "en", "zh-CN", "one", null, cts.Token).GetAwaiter().GetResult());
        Check(doc.Completed == 1, "取消提交了未完成的段落");
        fake.OnCall = null;
        DocumentTranslation.RunAsync(doc, fake, "en", "zh-CN", "one", null, default).GetAwaiter().GetResult();
        Check(fake.Inputs.Count == 4 && doc.IsComplete, "续译重复了已完成段落");
        DocumentTranslation.RunAsync(doc, fake, "en", "zh-CN", "changed", null, default).GetAwaiter().GetResult();
        Check(fake.Inputs.Count == 7, "换源没有从原文重新开始");
    }
    static void InvalidFiles(string root)
    {
        Check(DocumentTranslation.Load(PutText(root, "empty.txt", "123\n  \n")).Count == 0, "数字空文件不应调用翻译");
        Throws<InvalidDataException>(() => DocumentTranslation.Load(PutText(root, "bad.docx", "not zip")));
        var encrypted = Path.Combine(root, "drm.epub");
        Package(encrypted, EpubEntries().Append(("META-INF/encryption.xml", "<encryption><EncryptionMethod Algorithm='DRM'/></encryption>")));
        Throws<NotSupportedException>(() => DocumentTranslation.Load(encrypted));
        var duplicate = Path.Combine(root, "dup.docx"); Package(duplicate, [("same", "one"), ("same", "two")]);
        Throws<InvalidDataException>(() => DocumentTranslation.Load(duplicate));
        var xxe = Path.Combine(root, "xxe.epub");
        Package(xxe, EpubEntries().Select(e => e.Name == "OPS/chapters/a.xhtml" ? (e.Name, "<!DOCTYPE html [<!ENTITY external SYSTEM 'file:///C:/Windows/win.ini'>]><html xmlns='http://www.w3.org/1999/xhtml'><body><p>&external;</p></body></html>") : e));
        Throws<InvalidDataException>(() => DocumentTranslation.Load(xxe));
    }

    static void LocalHttpRoundtrip(string root)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string request = "";
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            using var data = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, timeout.Token);
                if (count == 0) throw new IOException("请求提前断开");
                data.Write(buffer, 0, count);
                var received = Encoding.UTF8.GetString(data.ToArray());
                var headerEnd = received.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0) continue;
                var match = Regex.Match(received[..headerEnd], @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
                if (!match.Success || data.Length < headerEnd + 4 + int.Parse(match.Groups[1].Value)) continue;
                request = received;
                break;
            }
            var body = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"你好世界\"}}]}");
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
        });
        try
        {
            var cfg = ProviderConfig.Create(ProviderKind.OpenAiCompat); cfg.Options["baseUrl"] = $"http://127.0.0.1:{port}/v1"; cfg.Options["model"] = "test-local"; cfg.Options["apiKey"] = "";
            var source = PutText(root, "http.txt", "Hello world");
            var history = new DocumentHistoryService(Path.Combine(root, "history"));
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            string output;
            try
            {
                var window = new DocumentTranslationWindow(history) { ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    window.Show(); window.SetOutputDirectory("");
                    window.Providers.Items.Clear();
                    window.Providers.Items.Add(new ComboBoxItem { Content = cfg.DisplayName, Tag = cfg });
                    window.Providers.SelectedIndex = 0;
                    ((LangPicker)window.FromHost.Content).SelectedCode = "en";
                    ((LangPicker)window.ToHost.Content).SelectedCode = "zh-CN";
                    PumpUntil(window.LoadFileAsync(source));
                    var loaded = (TranslationDocument)typeof(DocumentTranslationWindow).GetField("_document", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                    var write = loaded.Write;
                    loaded.Write = _ => throw new IOException("自测模拟磁盘保存失败");
                    // 从真实按钮进入，经过异步翻译、自动输出和历史持久化，不调用另存对话框。
                    window.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    WaitUi(() => window.StartButton.IsEnabled);
                    Check(history.Load().Count == 1, "按钮任务没有记录历史：" + window.Status.Text);
                    var entry = history.Load().Single();
                    Check(entry.Status == "SaveFailed" && (string)window.StartButton.Content == "重试保存", "保存失败未保留译文供重试：" + window.Status.Text);
                    Check(!Directory.GetFiles(root, "http_译文*").Any(), "失败仍写入了不完整输出");
                    loaded.Write = write;
                    // 本机服务器只响应一次；第二次点击若重复调用翻译，就会超时而非通过测试。
                    window.StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    WaitUi(() => window.StartButton.IsEnabled);
                    entry = history.Load().Single();
                    Check(entry.Status == "Completed", "UI 未完成自动输出：" + window.Status.Text);
                    output = entry.OutputPath;
                    Check(Path.GetDirectoryName(output) == root && File.ReadAllText(source) == "Hello world", "自动输出位置错误或原文被覆盖");
                    Check(window.OpenResult.Visibility == Visibility.Visible, "成功后未显示打开译文");
                    Check(window.CanDrop(new DataObject(DataFormats.FileDrop, new[] { source })), "有效拖入被拒绝");
                    var card = (Border)window.HistoryList.Children[0];
                    var actions = (WrapPanel)((StackPanel)card.Child).Children[3];
                    var missing = Path.Combine(root, "renamed.txt");
                    File.Move(output, missing);
                    ((Button)actions.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(window.Status.Text.Contains("移动"), "历史文件丢失没有明确提示");
                    File.Move(missing, output);
                }
                finally { window.Close(); }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            server.GetAwaiter().GetResult();
            Check(request.StartsWith("POST /v1/chat/completions HTTP/1.1"), "本地接口路径或协议错误");
            Check(!request.Contains("Authorization:"), "本地无 Key 却发了认证头");
            using var json = JsonDocument.Parse(request[(request.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
            Check(json.RootElement.GetProperty("model").GetString() == "test-local", "模型名未发送");
            Check(File.ReadAllText(output) == "你好世界", "HTTP 实际返回译文没有写入");
            Check(Net.Client.Timeout == Timeout.InfiniteTimeSpan, "长文仍受全局 30 秒限制");
            using var stalledStop = new CancellationTokenSource();
            var stalled = Task.Run(async () =>
            {
                try
                {
                    for (var i = 0; i < 2; i++)
                    {
                        using var client = await listener.AcceptTcpClientAsync(stalledStop.Token);
                        await Task.Delay(500, stalledStop.Token);
                    }
                }
                catch (OperationCanceledException) when (stalledStop.IsCancellationRequested) { }
            });
            try
            {
                var error = Throws<ProviderException>(() => Net.GetStringAsync($"http://127.0.0.1:{port}/timeout", 100, default).GetAwaiter().GetResult());
                Check(error.Message.Contains("超时"), "请求级超时没有生效");
                using var cancel = new CancellationTokenSource(50);
                Throws<OperationCanceledException>(() => Net.GetStringAsync($"http://127.0.0.1:{port}/cancel", 5000, cancel.Token).GetAwaiter().GetResult());
            }
            finally { stalledStop.Cancel(); stalled.GetAwaiter().GetResult(); }
        }
        finally { timeout.Cancel(); listener.Stop(); }
    }

    static void PumpUntil(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("WPF 文档任务未完成");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
    }
    static void WaitUi(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!ready())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("界面任务没有结束");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(5);
        }
    }
    static void WindowRoundtrip(string root)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { WindowRoundtripOnDispatcher(root); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    static void WindowRoundtripOnDispatcher(string root)
    {
        var path = PutText(root, "界面示例.md", "# Hello world\n\nHello **world**\n");
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            ThemeService.ApplyTheme(theme);
            var history = new DocumentHistoryService(Path.Combine(root, "ui-history"));
            history.Add(new DocumentHistoryEntry { SourcePath = path, OutputPath = Path.Combine(root, "界面示例_译文_zh-CN.md"), ProviderName = "Ollama（本地）", TargetLanguage = "zh-CN", Total = 2, Completed = 2 });
            var window = new DocumentTranslationWindow(history) { ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); PumpUntil(window.LoadFileAsync(path)); window.UpdateLayout();
                var content = (FrameworkElement)window.Content;
                Check(content.ActualWidth > 400 && content.ActualHeight > 300, "文件窗口布局失败");
                Check(window.DropArea.ActualHeight >= 180 && window.DropArea.ActualWidth > 370, "拖入区域过小");
                Check(window.CanDrop(new DataObject(DataFormats.FileDrop, new[] { path })), "拖入没有接受 Markdown");
                Check(!window.CanDrop(new DataObject(DataFormats.FileDrop, new[] { path, path })), "多文件拖入未拒绝");
                Check(!window.CanDrop(new DataObject(DataFormats.FileDrop, new[] { "bad.exe" })), "不支持类型未拒绝");
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                if (Environment.GetEnvironmentVariable("FLASHTRANS_DOCUMENT_SHOTS") is { Length: > 0 } folder)
                {
                    Directory.CreateDirectory(folder);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(folder, $"document-{theme}.png")); encoder.Save(file);
                }
                window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                var start = window.StartButton.TransformToAncestor(window).Transform(new Point());
                Check(start.Y + window.StartButton.ActualHeight < window.ActualHeight && start.X > 0, "小窗口主按钮被裁掉");
                Check(window.OutputFolder.ActualWidth > 180, "输出路径区域被挤没");
                if (Environment.GetEnvironmentVariable("FLASHTRANS_DOCUMENT_SHOTS") is { Length: > 0 } smallFolder)
                    Render(window, Path.Combine(smallFolder, $"document-{theme}-small.png"));
                if (theme == AppTheme.Dark)
                {
                    var oldDefault = SettingsService.Instance.Current.DocumentOutputDirectory;
                    try
                    {
                        window.SetOutputDirectory(root); window.DefaultFolder.IsChecked = true;
                        var saved = JsonSerializer.Deserialize(File.ReadAllText(SettingsService.Instance.ConfigPath), SettingsJson.Default.AppSettings)!;
                        Check(saved.DocumentOutputDirectory == root, "勾选默认目录后未落盘");
                        var reopened = new DocumentTranslationWindow(history);
                        try { Check(reopened.OutputFolder.Text == root, "重开窗口未使用默认目录"); }
                        finally { reopened.Close(); }
                        window.DefaultFolder.IsChecked = false;
                        Check(SettingsService.Instance.Current.DocumentOutputDirectory == "", "取消默认未恢复源目录");
                    }
                    finally { SettingsService.Instance.Current.DocumentOutputDirectory = oldDefault; SettingsService.Instance.Save(); }
                }
            }
            finally { window.Close(); }
        }
        ThemeService.ApplyTheme(AppTheme.Dark);
        var main = new MainWindow(new AppHost()) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            main.Show();
            var tools = (StackPanel)main.FindName("ToolHost");
            Check(tools.Children[0] is Button { Content: StackPanel } && tools.Children[1] is System.Windows.Controls.Primitives.ToggleButton { Content: "双语对照" }, "文件入口不在双语对照左侧");
            Check(((StackPanel)main.FindName("InputTools")).Children.Count == 3, "旧输入区入口未移除");
            foreach (var width in new[] { 380.0, 560.0, 1000.0 })
            {
                main.Width = width; main.UpdateLayout();
                var languages = (FrameworkElement)main.FindName("LangHost");
                var langRect = languages.TransformToAncestor(main).TransformBounds(new Rect(languages.RenderSize));
                var toolsRect = tools.TransformToAncestor(main).TransformBounds(new Rect(tools.RenderSize));
                Check(!langRect.IntersectsWith(toolsRect), "主窗口标题栏重叠：" + width);
                Check(toolsRect.Right <= main.ActualWidth, "右上角工具条溢出：" + width);
                if (Environment.GetEnvironmentVariable("FLASHTRANS_DOCUMENT_SHOTS") is { Length: > 0 } folder)
                    Render(main, Path.Combine(folder, $"main-{width}.png"));
            }
        }
        finally { main.Close(); }
    }

    static void Render(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }

    static void InputTextHeight(string root)
    {
        var window = new DocumentTranslationWindow(new DocumentHistoryService(root)) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            Visuals<Expander>(window).Single().IsExpanded = true;
            window.TimeoutBox.Text = "180";
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
            ThemeService.ApplyTheme(theme);
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            foreach (var size in new[] { 12.0, 14.0, 18.0, 24.0 })
            foreach (var enabled in new[] { true, false })
            {
                window.Options.IsEnabled = enabled;
                foreach (var box in new[] { window.OutputFolder, window.TimeoutBox })
                {
                    box.FontSize = size; box.LayoutTransform = new ScaleTransform(scale, scale);
                }
                window.UpdateLayout();
                foreach (var box in new[] { window.OutputFolder, window.TimeoutBox })
                {
                    var viewport = Visuals<ScrollContentPresenter>(box).First();
                    var bounds = viewport.TransformToAncestor(box).TransformBounds(new Rect(viewport.RenderSize));
                    var line = box.GetRectFromCharacterIndex(0);
                    var message = $"{box.Name} 字号 {size} / 缩放 {scale} / 启用 {enabled}：字行 {line}，可视区 {bounds}";
                    Check(!line.IsEmpty && line.Height > 0, "没有测到文字：" + message);
                    Check(line.Top >= bounds.Top - 0.5 && line.Bottom <= bounds.Bottom + 0.5, "文字被裁切：" + message);
                }
            }
            if (Environment.GetEnvironmentVariable("FLASHTRANS_DOCUMENT_SHOTS") is { Length: > 0 } folder)
            {
                foreach (var box in new[] { window.OutputFolder, window.TimeoutBox })
                { box.FontSize = 12; box.LayoutTransform = Transform.Identity; }
                window.UpdateLayout();
                var width = window.OutputFolder.ActualWidth + 20;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle((Brush)window.FindResource("Bg"), null, new Rect(0, 0, width, 120));
                    dc.DrawRectangle(new VisualBrush(window.OutputFolder), null, new Rect(10, 10, window.OutputFolder.ActualWidth, window.OutputFolder.ActualHeight));
                    dc.DrawRectangle(new VisualBrush(window.TimeoutBox), null, new Rect(10, 70, window.TimeoutBox.ActualWidth, window.TimeoutBox.ActualHeight));
                }
                var dpi = VisualTreeHelper.GetDpi(window);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(120 * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(folder);
                using var file = File.Create(Path.Combine(folder, $"text-fields-{theme}.png")); encoder.Save(file);
            }
            }
        }
        finally { window.Close(); ThemeService.ApplyTheme(AppTheme.Dark); }
    }

    static void TargetLanguagePersistence(string root)
    {
        var service = SettingsService.Instance;
        var oldTarget = service.Current.TargetLang;
        var oldDocumentTarget = service.Current.DocumentTargetLang;
        var history = new DocumentHistoryService(root);
        const System.Reflection.BindingFlags privateInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            foreach (var bad in new[] { "auto", "unknown", "", "  " })
            {
                var settings = JsonSerializer.Deserialize("{\"version\":7}", SettingsJson.Default.AppSettings)!;
                Check(settings.DocumentTargetLang == "", "旧配置没有使用兼容默认值");
                Check(SettingsService.Migrate(settings) && settings.Version == 8, "目标语言迁移失败");
                settings.DocumentTargetLang = bad; SettingsService.Normalize(settings);
                Check(settings.DocumentTargetLang == "", "非法目标语言未回退");
            }
            var valid = new AppSettings { Version = 7, DocumentTargetLang = " EN " };
            SettingsService.Migrate(valid); SettingsService.Normalize(valid);
            Check(valid.DocumentTargetLang == "en", "迁移覆盖有效选择或归一化失败");
            service.Current.TargetLang = "en"; service.Current.DocumentTargetLang = "";
            var window = new DocumentTranslationWindow(history) { ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                var picker = (LangPicker)window.ToHost.Content;
                Check(picker.SelectedCode == "en", "首次打开未沿用主窗口语言");
                // 经过真实语言列表的 Commit，证明用户选择触发保存，不是仅验证模型序列化。
                picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var list = (ListBox)typeof(LangPicker).GetField("_list", privateInstance)!.GetValue(picker)!;
                list.SelectedItem = list.Items.OfType<Lang>().Single(l => l.Code == "ja");
                typeof(LangPicker).GetMethod("Commit", privateInstance)!.Invoke(picker, null);
                Check(service.Current.DocumentTargetLang == "ja" && service.Current.TargetLang == "en", "选择没有保存或误改主窗口");
                var disk = JsonSerializer.Deserialize(File.ReadAllText(service.ConfigPath), SettingsJson.Default.AppSettings)!;
                Check(disk.DocumentTargetLang == "ja" && disk.TargetLang == "en", "目标语言没有落盘");
            }
            finally { window.Close(); }
            service.Current.DocumentTargetLang = "";
            service.Load(); // 重新走启动时的读取、迁移与归一化路径。
            service.Current.TargetLang = "fr";
            var source = PutText(root, "history.txt", "Hello");
            history.Add(new DocumentHistoryEntry { SourcePath = source, SourceLanguage = "en", TargetLanguage = "ko", Status = "Cancelled" });
            var reopened = new DocumentTranslationWindow(history) { ShowActivated = false, ShowInTaskbar = false };
            try
            {
                reopened.Show();
                Check(((LangPicker)reopened.ToHost.Content).SelectedCode == "ja", "重开/重载后目标语言丢失或被主窗口覆盖");
                var actions = (WrapPanel)((StackPanel)((Border)reopened.HistoryList.Children[0]).Child).Children[3];
                ((Button)actions.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUi(() => reopened.DropArea.IsEnabled);
                Check(((LangPicker)reopened.ToHost.Content).SelectedCode == "ko" && service.Current.DocumentTargetLang == "ko", "历史任务目标语言未记住");
                Check(service.Current.TargetLang == "fr", "恢复历史改变了主窗口语言");
                var disk = JsonSerializer.Deserialize(File.ReadAllText(service.ConfigPath), SettingsJson.Default.AppSettings)!;
                Check(disk.DocumentTargetLang == "ko", "历史选择未落盘");
            }
            finally { reopened.Close(); }
        }
        finally
        {
            service.Current.TargetLang = oldTarget; service.Current.DocumentTargetLang = oldDocumentTarget; service.Save();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    static void AutomaticCopies(string root)
    {
        var source = PutText(root, "input.txt", "Hello"); var doc = Translate(source);
        var first = doc.SaveCopy("", "zh-CN"); var second = doc.SaveCopy("", "zh-CN");
        Check(Path.GetDirectoryName(first) == root && second.EndsWith(" (2).txt"), "自动保存目录或避让错误");
        Check(File.ReadAllText(source) == "Hello" && File.ReadAllText(first) == "你好", "自动输出损坏原件");
        var custom = Path.Combine(root, "译文"); Directory.CreateDirectory(custom);
        Check(Path.GetDirectoryName(doc.SaveCopy(custom, "zh-CN")) == custom, "自定义目录未生效");
        Throws<DirectoryNotFoundException>(() => doc.SaveCopy(Path.Combine(root, "missing"), "en"));
        var write = doc.Write; doc.Write = _ => throw new IOException("自测模拟保存故障");
        Throws<IOException>(() => doc.SaveCopy(custom, "en"));
        Check(!Directory.GetFiles(custom, ".flashtrans-*").Any(), "保存失败残留临时文件");
        doc.Write = write; Check(File.Exists(doc.SaveCopy(custom, "en")), "保存失败后不能重试");
    }
    static void HistoryPersistence(string root)
    {
        var history = new DocumentHistoryService(root);
        var source = PutText(root, "source.txt", "Hello");
        var entry = new DocumentHistoryEntry { SourcePath = source, TargetLanguage = "zh-CN", ProviderName = "本机", Status = "Failed" };
        history.Add(entry);
        var next = new DocumentHistoryService(root);
        Check(next.Load().Single().Status == "Failed", "重启后历史丢失");
        next.Add(entry with { Status = "Completed", OutputPath = source });
        Check(next.Load().Count == 1 && next.Load().Single().Status == "Completed", "重试保存没有更新同一任务");
        for (var i = 0; i < 102; i++) next.Add(entry with { Id = i.ToString(), Time = entry.Time.AddSeconds(i + 1) });
        Check(next.Load().Count == 100 && next.Load()[0].Id == "101", "历史顺序或上限错误");
        var json = File.ReadAllText(Path.Combine(root, "document-history.json"));
        Check(!json.Contains("Hello") && !json.Contains("apiKey"), "历史泄漏正文或密钥");
        next.Clear(); Check(next.Load().Count == 0 && File.Exists(source), "清空历史删除了文档");
        File.WriteAllText(Path.Combine(root, "document-history.json"), "broken");
        Check(next.Load().Count == 0 && next.LoadWarning is not null, "损坏历史没有提示");
        Throws<IOException>(() => next.Add(entry));
        Check(File.ReadAllText(Path.Combine(root, "document-history.json")) == "broken", "损坏记录被覆盖");
        next.Clear(); next.Add(entry); Check(next.Load().Count == 1, "清空损坏历史后仍不能记录");
    }
    static void OutputSettings(string root)
    {
        var settings = JsonSerializer.Deserialize("{\"version\":6}", SettingsJson.Default.AppSettings)!;
        Check(settings.DocumentOutputDirectory == "", "旧配置未默认使用源目录");
        Check(SettingsService.Migrate(settings) && settings.Version == AppSettings.CurrentVersion, "文件翻译配置迁移失败");
        settings.DocumentOutputDirectory = root;
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(settings, SettingsJson.Default.AppSettings), SettingsJson.Default.AppSettings)!;
        SettingsService.Normalize(copy); Check(copy.DocumentOutputDirectory == root, "默认输出目录未保存");
        copy.DocumentOutputDirectory = "relative"; SettingsService.Normalize(copy); Check(copy.DocumentOutputDirectory == "", "相对目录未归一化");
        Check(typeof(MainWindow).Assembly.GetName().Version == new Version(1, 9, 1, 0), "主程序版本未升级到 1.9.1");
    }

    sealed class TestTranslator : ITranslator
    {
        public string Id => "test";
        public string Name => "离线自测";
        public ProviderKind Kind => ProviderKind.OpenAiCompat;
        public int TimeoutMs => 10000;
        public bool BatchTargets => false;
        public string? ConfigError => null;
        public List<string> Inputs { get; } = [];
        internal Func<string, string> Transform = s => s.Replace("Hello", "你好").Replace("world", "世界");
        internal Action<int>? OnCall;
        public Task<TranslateResult> TranslateAsync(TranslateRequest req, CancellationToken ct)
        {
            Inputs.Add(req.Text); OnCall?.Invoke(Inputs.Count);
            return Task.FromResult(new TranslateResult { ProviderId = Id, Texts = new() { [req.SingleTarget] = Transform(req.Text) } });
        }
    }
}
