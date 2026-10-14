using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

/// <summary>只使用生成的样例；规则、像素、窗口交互和本地引擎分开验证，不请求翻译服务。</summary>
static class RedactionProbe
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll(Action<string, Action> step)
    {
        var settings = SettingsService.Instance.Current;
        var oldAuto = settings.CaptureAutoRedact;
        var oldKeywords = settings.CaptureRedactionKeywords;
        var oldLocked = settings.CaptureRedactionKeywordsLocked;
        settings.CaptureAutoRedact = false;
        settings.CaptureRedactionKeywords = "";
        settings.CaptureRedactionKeywordsLocked = false;
        try
        {
            step("打码：敏感格式、校验码和普通文本误判回归", Rules);
            step("打码：关键词默认值、加密往返及损坏时阻止检测", Settings);
            step("打码：整行合并、外扩裁切和 OCR 坐标变换", Geometry);
            step("打码：多 DPI 像素覆写、上层马赛克和撤销重做", Pixels);
            step("打码：PNG、剪贴板和贴图仅保留脱敏像素", Outputs);
            step("打码：工具条真实窗口和窄屏布局", Toolbar);
            step("打码：自动检测等待、所有静态动作及长图/录制边界", Workflow);
            step("打码：检测失败必须明确转手动才能导出", Failure);
            step("打码：关闭、取消和旧请求不能回填遮挡", Cancellation);
            step("打码：RapidOCR 与 Windows OCR 真实识别及脱敏后复识别", Engines);
        }
        finally
        {
            settings.CaptureAutoRedact = oldAuto; settings.CaptureRedactionKeywords = oldKeywords;
            settings.CaptureRedactionKeywordsLocked = oldLocked;
        }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void Rules()
    {
        string[] positives = ["手机号：13800123456", "+86 138-0012-3456", "１３８００１２３４５６",
            "demo.person@example.com", "demo.person @ example . com", "密码：sample-only-value",
            "密 码 : sample-only-value", "api_key = sample_only_value", "password sample-only-value",
            "验证码：482917", "密码：a", "pwd=12", "token: SAMPLE_ONLY_NOT_A_REAL_TOKEN", "sk-0123456789abcdefghijklmnop",
            "4111 1111 1111 1111", "5500-0000-0000-0004", "11010519491231002X",
            "13800123456 13900123456", "138 0012 3456 139 0012 3456", "4111111111111111 5500000000000004"];
        for (var i = 0; i < positives.Length; i++)
            Check(SensitiveContentDetector.ContainsSensitive(positives[i]), $"正样例 {i} 未命中");
        string[] negatives = ["普通说明文字", "version 1.9.1", "2026-10-14", "192.168.1.10", "123456",
            "int count = 2048;", "password", "4111111111111112", "110105194912310020",
            "0000000000000000", "order 1234567890123456789", "138OO123456", "example.com"];
        for (var i = 0; i < negatives.Length; i++)
            Check(!SensitiveContentDetector.ContainsSensitive(negatives[i]), $"普通/无效样例 {i} 被误判");
        var keys = SensitiveContentDetector.Keywords("内部计划;Private Project；内部计划");
        Check(keys.Length == 2 && SensitiveContentDetector.ContainsSensitive("内 部 计 划", keys)
            && SensitiveContentDetector.ContainsSensitive("private project", keys), "关键词规范化未生效");
        Check(!SensitiveContentDetector.ContainsSensitive("2026", ["a.*"]), "关键词被当成正则表达式");
        Check(SensitiveContentDetector.Keywords("\u200b \u200b").Length == 0, "不可见关键词意外匹配全部文字");
    }

    static void Settings()
    {
        var defaults = JsonSerializer.Deserialize("{}", SettingsJson.Default.AppSettings)!;
        Check(!defaults.CaptureAutoRedact && defaults.CaptureRedactionKeywords == "", "旧配置意外启用打码或含关键词");
        const string phrase = "合成测试词;Private Project";
        var encrypted = SettingsService.ProtectRedactionKeywords(phrase);
        Check(encrypted.StartsWith("dpapi:") && !encrypted.Contains(phrase), "关键词未加密");
        Check(SettingsService.UnprotectRedactionKeywords(encrypted) == phrase, "关键词解密未往返");
        var broken = "dpapi:not-valid-base64";
        Check(SettingsService.UnprotectRedactionKeywords(broken) == broken, "损坏关键词被静默清空");
        var locked = new AppSettings { CaptureRedactionKeywords = broken };
        typeof(SettingsService).GetMethod("Decrypt", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [locked]);
        Check(locked.CaptureRedactionKeywordsLocked, "不可解密关键词未标记为不可用");
        typeof(SettingsService).GetMethod("Encrypt", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [locked]);
        Check(locked.CaptureRedactionKeywords == broken, "无法解密的密文未保留");
        const string literalPrefix = "dpapi:literal-keyword";
        var protectedPrefix = SettingsService.ProtectRedactionKeywords(literalPrefix);
        Check(protectedPrefix != literalPrefix && SettingsService.UnprotectRedactionKeywords(protectedPrefix) == literalPrefix,
            "dpapi 前缀的字面关键词未加密或无法往返");
        defaults.CaptureAutoRedact = true;
        defaults.CaptureRedactionKeywords = phrase;
        defaults.CkRedact = "Ctrl+B";
        defaults.CkAutoRedact = "Ctrl+Shift+M";
        typeof(SettingsService).GetMethod("Encrypt", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [defaults]);
        var wire = JsonSerializer.Serialize(defaults, SettingsJson.Default.AppSettings);
        Check(!wire.Contains("Private Project"), "配置副本泄露关键词");
        var restored = JsonSerializer.Deserialize(wire, SettingsJson.Default.AppSettings)!;
        typeof(SettingsService).GetMethod("Decrypt", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [restored]);
        Check(restored.CaptureAutoRedact && restored.CaptureRedactionKeywords == phrase
            && restored.CkRedact == "Ctrl+B" && restored.CkAutoRedact == "Ctrl+Shift+M", "配置读写不完整");
    }

    static void Geometry()
    {
        OcrTextRegion[] lines = [new("Phone:", new Rect(0, 0, 48, 20)),
            new("138 0012 3456", new Rect(55, 0, 140, 20)), new("ordinary note", new Rect(0, 60, 100, 20))];
        var areas = SensitiveContentDetector.Detect(lines, 200, 100);
        Check(areas.Count == 1 && areas[0].Contains(new Rect(0, 0, 195, 20))
            && areas[0].Bottom < 60 && areas[0].Right <= 200, "合并、外扩或裁切错误");
        var mapped = OcrService.MapOcrBounds(new Rect(30, 10, 20, 10), 90, 100, 80, 200, 160);
        Check(Math.Abs(mapped.X - 140) < .001 && Math.Abs(mapped.Y - 40) < .001
            && Math.Abs(mapped.Width - 20) < .001 && Math.Abs(mapped.Height - 40) < .001, "旋转坐标未映射回原图");
        var invalid = false;
        try { SensitiveContentDetector.Detect([new("13800123456", Rect.Empty)], 100, 100); }
        catch (InvalidOperationException) { invalid = true; }
        Check(invalid, "无位置文字被静默忽略");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var cancelled = false;
        try { SensitiveContentDetector.Detect(lines, 200, 100, ct: cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "检测未响应取消");
    }

    static CapturedImage Pattern(int width = 800, int height = 300)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(40 + i % 180); pixels[i + 1] = 210; pixels[i + 2] = 170; pixels[i + 3] = 255;
        }
        return new CapturedImage(width, height, pixels);
    }

    static void Black(CapturedImage image, Rect area)
    {
        for (var y = (int)Math.Floor(area.Top); y < Math.Ceiling(area.Bottom); y++)
            for (var x = (int)Math.Floor(area.Left); x < Math.Ceiling(area.Right); x++)
            {
                var i = (y * image.Width + x) * 4;
                Check(image.Pixels[i] == 0 && image.Pixels[i + 1] == 0 && image.Pixels[i + 2] == 0
                    && image.Pixels[i + 3] == 255, "遮挡含非黑色或半透明像素");
            }
    }

    static void Equal(CapturedImage expected, CapturedImage actual) =>
        Check(expected.Width == actual.Width && expected.Height == actual.Height
            && expected.Pixels.AsSpan().SequenceEqual(actual.Pixels), "像素往返发生变化");

    static void Pixels()
    {
        foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75, 2 })
        {
            var shot = Pattern();
            var layer = (CaptureSelectionLayer)CaptureOverlay.LayerForShot(shot,
                new Size(800 / scale, 300 / scale), new Rect(13.4 / scale, 9.4 / scale, 600.4 / scale, 190.4 / scale));
            var original = layer.Export()!;
            var area = new Rect(25, 30, 175, 34);
            Check(layer.AddRedactions([area]) == 1 && layer.AddRedactions([area]) == 0, "重复检测增加了重复遮挡");
            var redaction = layer.Items.OfType<RedactionAnnotation>().Single();
            layer.AddAnnotation(new MosaicAnnotation { Bounds = redaction.Bounds });
            var masked = layer.Export()!; Black(masked, area);
            Equal(CaptureOverlay.CropPixels(original, 300, 100, 40, 40)!, CaptureOverlay.CropPixels(masked, 300, 100, 40, 40)!);
            layer.Undo(); layer.Undo(); Equal(original, layer.Export()!);
            Check(layer.Redo(), "遮挡不能重做"); Black(layer.Export()!, area);
            layer.SelectForTest(redaction); layer.PenColor = Colors.White;
            Check(!layer.Restyle(), "实心遮挡允许改为透明/浅色");
            Check(layer.Nudge(new Vector(5 / scale, 0)), "遮挡不能移动");
            Black(layer.Export()!, new Rect(30, 30, 175, 34));
            Check(layer.DeleteActive(), "遮挡不能删除"); Equal(original, layer.Export()!);
        }
    }

    static CapturedImage FromBitmap(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
        return new CapturedImage(converted.PixelWidth, converted.PixelHeight, bytes);
    }

    static void Outputs()
    {
        var source = Pattern();
        var layer = (CaptureSelectionLayer)CaptureOverlay.LayerForShot(source, new Size(800, 300), new Rect(0, 0, 800, 300));
        layer.AddRedactions([new Rect(20, 20, 240, 50)]);
        var image = layer.Export()!;
        var path = Path.Combine(Path.GetTempPath(), "FlashTrans-redaction-" + Guid.NewGuid().ToString("N") + ".png");
        using var clipboard = ClipboardSnapshot.Capture();
        uint sequence = 0;
        var pin = new PinnedShotWindow(image) { ShowActivated = false };
        try
        {
            image.SavePng(path);
            using (var stream = File.OpenRead(path))
                Equal(image, FromBitmap(BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0]));
            SelectionReader.SetImage(image);
            sequence = Win32.GetClipboardSequenceNumber();
            Equal(image, FromBitmap(Clipboard.GetImage()!));
            Check(string.IsNullOrEmpty(SelectionReader.ReadText()), "图片复制带入旧原文");
            pin.Show(); pin.UpdateLayout();
            Equal(image, Field<CapturedImage>(pin, "_image"));
            Black(image, new Rect(20, 20, 240, 50));
        }
        finally
        {
            pin.Close();
            if (sequence != 0) clipboard.Restore(sequence);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    static CaptureSelectionLayer Layer(CaptureOverlay win) => Field<CaptureSelectionLayer>(win, "_layer");

    static CaptureOverlay Window(Func<CapturedImage, CancellationToken, Task<IReadOnlyList<OcrTextRegion>>>? read = null,
        int width = 900)
    {
        var win = new CaptureOverlay(Pattern(900, 400), default, read ?? ((_, _) => Task.FromResult(SampleRegions())))
        {
            Width = width, Height = 400, Left = 0, Top = 0, ShowActivated = false,
        };
        win.Show(); win.UpdateLayout();
        Layer(win).PresetSelection(new Rect(10, 10, Math.Min(700, width - 20), 220));
        Call(win, "OnSelectionChanged"); win.UpdateLayout();
        return win;
    }

    static IReadOnlyList<OcrTextRegion> SampleRegions() => [new("demo.person@example.com", new Rect(35, 30, 250, 30))];

    static void Await(Task task)
    {
        Until(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    static void Until(Func<bool> done)
    {
        if (done()) return;
        var frame = new DispatcherFrame();
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || sw.Elapsed.TotalSeconds > 60) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Check(done(), "等待打码流程超时");
    }

    static void Toolbar()
    {
        foreach (var width in new[] { 900, 640 })
        {
            var win = Window(width: width);
            try
            {
                var row = Field<Panel>(win, "_actionRow");
                var auto = row.Children.OfType<Button>().Single(b => b.Content as string == "自动打码");
                auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(() => Field<object>(win, "_redactionState").ToString() is "Ready" or "Failed");
                Check(Field<object>(win, "_redactionState").ToString() == "Ready", "真实工具条按钮没有触发打码");
                win.UpdateLayout();
                var canvas = Field<Canvas>(win, "_canvas");
                var bar = Field<Border>(win, "_toolbar");
                Check(bar.ActualWidth <= canvas.ActualWidth && bar.ActualHeight < canvas.ActualHeight, "窄屏工具条超出画布");
                foreach (var button in row.Children.OfType<Button>())
                {
                    var bounds = button.TransformToAncestor(canvas).TransformBounds(new Rect(button.RenderSize));
                    Check(bounds.Left >= -.1 && bounds.Right <= canvas.ActualWidth + .1
                        && bounds.Top >= -.1 && bounds.Bottom <= canvas.ActualHeight + .1, "按钮被挤出窗口");
                }
                if (width == 900 && Environment.GetEnvironmentVariable("FLASHTRANS_REDACTION_PREVIEW") is { Length: > 0 } path)
                {
                    var bitmap = new RenderTargetBitmap((int)canvas.ActualWidth, (int)canvas.ActualHeight,
                        96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(canvas);
                    FromBitmap(bitmap).SavePng(path);
                }
            }
            finally { win.Close(); }
        }
    }

    static void Workflow()
    {
        var settings = SettingsService.Instance.Current;
        settings.CaptureAutoRedact = true;
        var automatic = Window();
        try
        {
            Call(automatic, "Finish", CaptureAction.Copy);
            Check(Field<CaptureOutcome>(automatic, "_result").Action == CaptureAction.None, "自动检测等待期间放行图片");
            Until(() => Field<object>(automatic, "_redactionState").ToString() is "Ready" or "Failed");
            Check(Layer(automatic).HasRedactions, "选区稳定后未自动检测");
            Layer(automatic).PresetSelection(new Rect(20, 10, 600, 220));
            Call(automatic, "OnSelectionChanged");
            Call(automatic, "Finish", CaptureAction.Copy);
            Check(Field<CaptureOutcome>(automatic, "_result").Action == CaptureAction.None, "移动选区后使用了旧检测状态");
        }
        finally { automatic.Close(); settings.CaptureAutoRedact = false; }

        foreach (var action in new[] { CaptureAction.Copy, CaptureAction.Save, CaptureAction.Pin,
                     CaptureAction.Ocr, CaptureAction.OcrCopy, CaptureAction.OcrTranslate })
        {
            var win = Window();
            try
            {
                Await(win.RedactAsync());
                Check(Layer(win).HasRedactions, "未产生遮挡");
                Call(win, "FinishLongShot"); Call(win, "FinishRecord");
                var unfinished = Field<CaptureOutcome>(win, "_result");
                Check(!unfinished.WantsLongShot && !unfinished.WantsRecord, "长图或录制绕过了保护提醒");
                Call(win, "Finish", action);
                var result = Field<CaptureOutcome>(win, "_result");
                Check(result.Action == action && result.Image is not null, "静态动作未正常导出");
                Black(result.Image!, new Rect(35, 30, 250, 30));
            }
            finally { if (win.IsVisible) win.Close(); }
        }
    }

    static void Failure()
    {
        foreach (var empty in new[] { false, true })
        {
            var win = Window((_, _) => empty ? Task.FromResult<IReadOnlyList<OcrTextRegion>>([])
                : Task.FromException<IReadOnlyList<OcrTextRegion>>(new InvalidOperationException("RAW_TEXT_MUST_NOT_APPEAR")));
            try
            {
                Await(win.RedactAsync());
                Check(Field<object>(win, "_redactionState").ToString() == "Failed", "失败被当成成功");
                Check(!Field<TextBlock>(win, "_redactionStatus").Text.Contains("RAW_TEXT"), "错误提示泄露引擎原文");
                foreach (var action in new[] { CaptureAction.Copy, CaptureAction.Save, CaptureAction.Pin,
                             CaptureAction.Ocr, CaptureAction.OcrCopy, CaptureAction.OcrTranslate })
                    Call(win, "Finish", action);
                Check(Field<CaptureOutcome>(win, "_result").Action == CaptureAction.None, "失败后静默放行原图");
                var dock = (DockPanel)Field<Border>(win, "_redactionStatusRow").Child;
                dock.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Call(win, "Finish", CaptureAction.Copy);
                Check(Field<CaptureOutcome>(win, "_result").Action == CaptureAction.Copy, "明确改为手动后仍不能导出");
            }
            finally { if (win.IsVisible) win.Close(); }
        }
        var settings = SettingsService.Instance.Current;
        settings.CaptureRedactionKeywordsLocked = true;
        var locked = Window();
        try
        {
            Await(locked.RedactAsync()); Call(locked, "Finish", CaptureAction.Copy);
            Check(Field<CaptureOutcome>(locked, "_result").Action == CaptureAction.None
                && Field<object>(locked, "_redactionState").ToString() == "Failed", "不可解密的关键词仍放行了输出");
        }
        finally { locked.Close(); settings.CaptureRedactionKeywordsLocked = false; }
    }

    static void Cancellation()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<OcrTextRegion>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var win = Window((_, _) => Interlocked.Increment(ref calls) == 1 ? pending.Task : Task.FromResult(SampleRegions()));
        try
        {
            var first = win.RedactAsync();
            Until(() => Volatile.Read(ref calls) == 1);
            Call(win, "Finish", CaptureAction.Copy);
            Check(Field<CaptureOutcome>(win, "_result").Action == CaptureAction.None, "推理中放行图片");
            Check(!Field<Panel>(win, "_actionRow").IsEnabled && !Layer(win).IsHitTestVisible, "推理时仍可改变标注");
            var second = win.RedactAsync(); Await(second);
            pending.SetResult([new("13800123456", new Rect(400, 100, 150, 30))]);
            Await(first);
            Check(Layer(win).Items.OfType<RedactionAnnotation>().Count() == 1, "旧请求回填了额外遮挡");
            Check(Field<Panel>(win, "_actionRow").IsEnabled, "取消旧请求后界面仍被禁用");
        }
        finally { pending.TrySetResult([]); win.Close(); }

        var closedWork = new TaskCompletionSource<IReadOnlyList<OcrTextRegion>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = Window((_, _) => closedWork.Task);
        var work = closed.RedactAsync();
        closed.Close(); closedWork.TrySetResult(SampleRegions()); Await(work);
        Check(Layer(closed).AnnotationCount == 0 && !closed.IsVisible, "关闭后又回填或弹出了窗口");
    }

    static CapturedImage TextSample(bool chinese = false)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 210));
            var lines = chinese
                ? new[] { "邮箱：demo.person@example.com", "手机号：138 0012 3456", "说明：public release information" }
                : new[] { "Contact: demo.person@example.com", "Phone: 138 0012 3456", "Notes: public release information" };
            for (var i = 0; i < lines.Length; i++)
                dc.DrawText(new FormattedText(lines[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(chinese ? "Microsoft YaHei UI" : "Consolas"), 28, Brushes.Black, 1), new Point(20, 15 + i * 60));
        }
        var bitmap = new RenderTargetBitmap(900, 210, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return FromBitmap(bitmap);
    }

    static void Engines()
    {
        var engines = new List<string>();
        if (OcrService.RapidModelsPresent) engines.Add("rapid");
        else Console.WriteLine("       RapidOCR 模型缺失，未运行其实际识别验证");
        if (OcrService.IsAvailable) engines.Add("system");
        else Console.WriteLine("       Windows OCR 语言包缺失，未运行其实际识别验证");
        Check(engines.Count > 0, "没有可供实测的本地 OCR 引擎");
        foreach (var engine in engines)
        foreach (var chinese in new[] { false, true })
        {
            if (chinese && engine == "system" && !OcrService.AvailableLanguages.Any(l => l.StartsWith("zh", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("       Windows 中文 OCR 包缺失，未运行其中英混排识别验证");
                continue;
            }
            var image = TextSample(chinese);
            var language = chinese ? "zh-CN" : "en";
            var sw = Stopwatch.StartNew();
            var regions = Task.Run(() => OcrService.RecognizeRegionsAsync(image, language, engine: engine)).GetAwaiter().GetResult();
            var areas = SensitiveContentDetector.Detect(regions, image.Width, image.Height);
            Check(areas.Count >= 2 && areas.Any(r => r.Contains(new Point(200, 34)))
                && areas.Any(r => r.Contains(new Point(190, 94))), engine + " 未定位测试邮箱/手机号");
            Check(!areas.Any(r => r.Contains(new Point(240, 154))), engine + " 遮住了普通说明行");
            var layer = (CaptureSelectionLayer)CaptureOverlay.LayerForShot(image,
                new Size(image.Width, image.Height), new Rect(0, 0, image.Width, image.Height));
            layer.AddRedactions(areas);
            var masked = layer.Export()!;
            foreach (var area in areas) Black(masked, area);
            var text = Task.Run(() => OcrService.RecognizeAsync(masked, language, engine: engine)).GetAwaiter().GetResult();
            Check(!SensitiveContentDetector.ContainsSensitive(text) && !text.Contains("demo.person", StringComparison.OrdinalIgnoreCase),
                engine + " 脱敏后仍识别出了敏感样例");
            Check(text.Contains("public", StringComparison.OrdinalIgnoreCase), engine + " 普通文字也被丢弃");
            Console.WriteLine($"       {engine}/{language}：识别 {regions.Count} 块，遮挡 {areas.Count} 块，脱敏后复识别通过，{sw.ElapsedMilliseconds} ms");
        }
    }
}
