using System.Diagnostics;
using System.Globalization;
using System.Reflection;
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

/// <summary>隔离翻译网络耗时，实测窗口首帧与本机 OCR 的完成时间。</summary>
static class WindowStartupProbe
{
    public static void RunAll(Action<string, Action> step, bool preload = false)
    {
        var settings = SettingsService.Instance;
        var original = settings.Current;
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            // 不触碰外部翻译接口，也不改写用户的剪贴板。
            settings.Apply(new AppSettings { Providers = [], AggregateTab = true, OcrCopyText = false });
            step("启动耗时：直接翻译首次与复用", () => MeasurePopup(preload));
            if (preload)
                step("启动耗时：后台预加载本地 OCR", () =>
                {
                    var task = OcrService.WarmupAsync("auto", Languages.Auto);
                    WaitUntil(() => task.IsCompleted, "OCR 预加载");
                    task.GetAwaiter().GetResult();
                });
            step("启动耗时：OCR 结果窗", () => MeasureOcr(false));
            step("启动耗时：OCR 直接翻译窗", () => MeasureOcr(true));
            step("启动回归：OCR 结果就绪前不能复制或翻译，关闭后不回填", ResultLoading);
            step("启动回归：OCR 翻译关闭、替换、收起的归属隔离", PopupLoading);
            step("启动回归：真实 OCR 流程关闭后不再显示", CloseDuringRecognition);
            step("启动回归：空白截图原地报错，不触发翻译", EmptyRecognition);
        }
        finally
        {
            settings.Apply(original);
            settings.Save();
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    static void MeasurePopup(bool preload)
    {
        var host = new AppHost();
        PopupWindow? window = null;
        try
        {
            if (preload)
            {
                var foreground = Win32.GetForegroundWindow();
                host.PreloadPopup();
                window = Application.Current.Windows.OfType<PopupWindow>().Single();
                Require(!window.IsVisible, "预加载弹窗不应可见");
                Require(Win32.GetForegroundWindow() == foreground, "预加载不能抢前台焦点");
            }
            for (var round = 0; round < 3; round++)
            {
                var sw = Stopwatch.StartNew();
                host.ShowPopupFor("Window startup probe", new Point(40, 40));
                var returned = sw.Elapsed.TotalMilliseconds;
                window = Application.Current.Windows.OfType<PopupWindow>().Single(w => w.IsVisible);
                var rendered = false;
                EventHandler onRendered = (_, _) => rendered = true;
                CompositionTarget.Rendering += onRendered;
                try { WaitUntil(() => rendered, "翻译窗口首帧"); }
                finally { CompositionTarget.Rendering -= onRendered; }
                Console.WriteLine($"       直接翻译 #{round + 1}: 调起返回 {returned:F1}ms，WPF 渲染帧 {sw.Elapsed.TotalMilliseconds:F1}ms");
                window.ClosePopup();
                DrainDispatcher();
            }
        }
        finally { window?.Close(); }
    }

    static void MeasureOcr(bool translate)
    {
        var host = new AppHost();
        var method = typeof(AppHost).GetMethod("OcrAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var image = SampleImage();
        var sw = Stopwatch.StartNew();
        Window? window = null;
        double renderedAt = -1;
        EventHandler onRendering = (_, _) =>
        {
            if (renderedAt >= 0) return;
            window = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible &&
                (translate ? w is PopupWindow : w is OcrResultWindow));
            if (window is not null) renderedAt = sw.Elapsed.TotalMilliseconds;
        };
        CompositionTarget.Rendering += onRendering;
        try
        {
            var task = (Task)method.Invoke(host, [image, translate, false])!;
            WaitUntil(() => task.IsCompleted, "OCR 识别完成");
            task.GetAwaiter().GetResult();
            var completedAt = sw.Elapsed.TotalMilliseconds;
            WaitUntil(() => renderedAt >= 0, "OCR 窗口渲染");
            var text = translate ? Field<string>(window!, "_text")
                : Field<TextBox>(window!, "_box").Text;
            Require(text.Contains("startup", StringComparison.OrdinalIgnoreCase) && text.Contains("1234"),
                "实际 OCR 文本没有正确回填：" + text);
            Require(renderedAt < completedAt, "OCR 窗口仍在识别结束后才显示");
            Console.WriteLine($"       {(translate ? "OCR 翻译" : "OCR 结果")}: WPF 渲染帧 {renderedAt:F1}ms，识别流程完成 {completedAt:F1}ms");
        }
        finally
        {
            CompositionTarget.Rendering -= onRendering;
            foreach (var owned in Application.Current.Windows.OfType<Window>()
                         .Where(w => w is PopupWindow or OcrResultWindow).ToArray()) owned.Close();
            DrainDispatcher();
        }
    }

    static void ResultLoading()
    {
        var window = new OcrResultWindow("");
        var copied = 0;
        var translated = 0;
        window.Copy += _ => copied++;
        window.Translate += _ => translated++;
        try
        {
            window.ShowRecognizing();
            window.Show();
            var box = Field<TextBox>(window, "_box");
            Require(box.IsReadOnly && !Field<Button>(window, "_copyBtn").IsEnabled
                && !Field<Button>(window, "_translateBtn").IsEnabled, "识别中按钮未禁用");
            Invoke(window, "FireCopy");
            Invoke(window, "FireTranslate");
            Require(copied == 0 && translated == 0 && window.IsVisible, "识别中提前触发动作");
            window.SetRecognitionError("识别失败");
            Require(Field<TextBlock>(window, "_head").Text == "识别失败", "失败提示未显示");
            Require(window.SetRecognitionResult("  ready 1234"), "识别结果未接收");
            Require(!box.IsReadOnly && box.Text == "  ready 1234" && box.SelectionLength == box.Text.Length,
                "回填丢失缩进、可编辑状态或全选");
            window.Close();
            Require(!window.SetRecognitionResult("late"), "关闭后仍接受结果");
        }
        finally { if (window.IsVisible) window.Close(); DrainDispatcher(); }
    }

    static void PopupLoading()
    {
        var window = new PopupWindow(new AppHost());
        try
        {
            var token = window.ShowOcrLoading(new Point(40, 40));
            window.ClosePopup();
            Require(token.IsCancellationRequested && !window.CompleteOcr(token, "late"), "关闭后接受旧 OCR");
            Require(!window.IsVisible && !window.CanRestore, "关闭后又弹回或允许叫回");

            token = window.ShowOcrLoading(null);
            window.ShowFor("new translation", null);
            Require(!window.CompleteOcr(token, "old OCR"), "旧 OCR 覆盖直接翻译");
            Require(Field<string>(window, "_text") == "new translation", "新翻译被覆盖");
            DrainDispatcher();

            token = window.ShowOcrLoading(null);
            var next = window.ShowOcrLoading(null);
            Require(!window.CompleteOcr(token, "older OCR"), "旧 OCR 覆盖新 OCR");
            window.Left = 100;
            window.Top = 120;
            window.StashPopup();
            Require(window.CanRestore, "识别中收起后不能叫回");
            Require(window.CompleteOcr(next, "latest OCR"), "收起后不接受结果");
            DrainDispatcher();
            Require(!window.IsVisible && window.CanRestore, "完成时强制弹回收起的窗口");
            Require(window.RestorePopup() && Field<string>(window, "_text") == "latest OCR", "恢复丢失结果");
            Require(window.Left == 100 && window.Top == 120, "完成后窗口位置跳回");

            token = window.ShowOcrLoading(null);
            window.FailOcr(token, "失败测试");
            Require(Field<string>(window, "_text").Length == 0 && Field<object?>(window, "_batch") is null,
                "失败后残留上次文本或翻译结果");
            window.Close();
            Require(token.IsCancellationRequested && !window.CompleteOcr(token, "late"), "原生关闭后回填");
        }
        finally { if (window.IsVisible) window.Close(); DrainDispatcher(); }
    }

    static void CloseDuringRecognition()
    {
        foreach (var translate in new[] { false, true })
        {
            var host = new AppHost();
            var task = StartOcr(host, SampleImage(), translate);
            var window = Application.Current.Windows.OfType<Window>()
                .Single(w => translate ? w is PopupWindow : w is OcrResultWindow);
            try
            {
                window.Close();
                WaitUntil(() => task.IsCompleted, "关闭中的 OCR");
                task.GetAwaiter().GetResult();
                DrainDispatcher();
                Require(!Application.Current.Windows.OfType<Window>().Any(w => w is PopupWindow or OcrResultWindow),
                    "关闭 OCR 后窗口重新显示");
            }
            finally { if (window.IsVisible) window.Close(); }
        }
    }

    static void EmptyRecognition()
    {
        var pixels = Enumerable.Repeat((byte)255, 240 * 100 * 4).ToArray();
        var host = new AppHost();
        var task = StartOcr(host, new CapturedImage(240, 100, pixels), true);
        var window = Application.Current.Windows.OfType<PopupWindow>().Single();
        try
        {
            WaitUntil(() => task.IsCompleted, "空白截图 OCR");
            task.GetAwaiter().GetResult();
            Require(Field<string>(window, "_text").Length == 0, "空白截图被送进翻译");
            Require(Field<TextBlock>(window, "_status").Text.Contains("识别未完成"), "空白截图没有原地错误反馈");
        }
        finally { window.Close(); DrainDispatcher(); }
    }

    static Task StartOcr(AppHost host, CapturedImage image, bool translate) =>
        (Task)typeof(AppHost).GetMethod("OcrAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, [image, translate, false])!;

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    static void Invoke(object target, string name) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static CapturedImage SampleImage()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 120));
            dc.DrawText(new FormattedText("Window startup 1234", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Arial"), 36, Brushes.Black, 1), new Point(20, 30));
        }
        var bitmap = new RenderTargetBitmap(640, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[640 * 120 * 4];
        bitmap.CopyPixels(pixels, 640 * 4, 0);
        return new CapturedImage(640, 120, pixels);
    }

    static void WaitUntil(Func<bool> condition, string name)
    {
        var frame = new DispatcherFrame();
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) =>
        {
            if (condition() || sw.Elapsed > TimeSpan.FromSeconds(45)) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!condition()) throw new TimeoutException(name + "超时");
    }

    static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
