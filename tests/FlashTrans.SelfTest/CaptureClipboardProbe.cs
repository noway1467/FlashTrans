using System.Diagnostics;
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

static class CaptureClipboardProbe
{
    internal static int Child(string[] args)
    {
        try
        {
            if (args[0] == "--capture-clipboard-read-child")
            {
                ReadBack(Image());
                return 0;
            }
            using var owner = new MessageWindow("CaptureClipboard.Lock");
            if (!ClipboardSnapshot.Open(owner.Handle)) throw new InvalidOperationException("无法建立剪贴板占用测试前提");
            try
            {
                Console.WriteLine("ready");
                Console.Out.Flush();
                Thread.Sleep(int.Parse(args[1]));
            }
            finally { Win32.CloseClipboard(); }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    internal static void RunAll(Action<string, Action> step)
    {
        step("截图复制：剪贴板被独立进程占用 450ms 后仍能复制长图，外部进程可读回", ContendedCopy);
        step("长图预览：复制等待不阻塞，失败保留图片，重试成功才关闭", PreviewRetry);
        step("普通截图：复制失败打开保留图片的预览，解除占用后可重试", NormalCopyFailure);
    }

    static CapturedImage Image()
    {
        const int width = 1024, height = 4521;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(x * 7 + y);
                pixels[i + 1] = (byte)(x + y * 13);
                pixels[i + 2] = (byte)(x * 19 + y * 3);
                pixels[i + 3] = 255;
            }
        return new CapturedImage(width, height, pixels);
    }

    static Process StartChild(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(CaptureClipboardProbe).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    static Process LockClipboard(int milliseconds)
    {
        var child = StartChild("--capture-clipboard-lock-child", milliseconds.ToString());
        var ready = child.StandardOutput.ReadLineAsync();
        try
        {
            PumpUntil(() => ready.IsCompleted, "占用剪贴板的独立进程没有就绪");
            if (ready.GetAwaiter().GetResult() != "ready")
                throw new InvalidOperationException("占用进程无法打开剪贴板：" + child.StandardError.ReadToEnd());
            return child;
        }
        catch { Release(child); child.Dispose(); throw; }
    }

    static void Release(Process child)
    {
        Check(child.WaitForExit(10000), "剪贴板测试进程未退出");
        Check(child.ExitCode == 0, "剪贴板测试进程失败：" + child.StandardError.ReadToEnd());
    }

    static void ContendedCopy()
    {
        using var original = ClipboardSnapshot.Capture();
        var sequence = original.Sequence;
        try
        {
            var image = Image();
            using var child = LockClipboard(450);
            try
            {
                SelectionReader.SetImage(image);
                sequence = Win32.GetClipboardSequenceNumber();
            }
            finally { Release(child); }
            ReadBack(image);
            using var reader = StartChild("--capture-clipboard-read-child");
            Check(reader.WaitForExit(10000) && reader.ExitCode == 0,
                "独立进程未能读回长图：" + reader.StandardError.ReadToEnd());
        }
        finally { original.Restore(sequence); }
    }

    static void PreviewRetry()
    {
        using var original = ClipboardSnapshot.Capture();
        var sequence = original.Sequence;
        var settings = SettingsService.Instance.Current;
        var width = settings.LongShotPreviewWidth;
        var height = settings.LongShotPreviewHeight;
        LongShotWindow? window = null;
        try
        {
            var image = Image();
            typeof(AppHost).GetMethod("ShowLongShotPreview", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(new AppHost(), [image, "复制重试回归"]);
            window = Application.Current.Windows.OfType<LongShotWindow>().Single();
            window.UpdateLayout();
            var copy = Descendants<Button>(window).Single(b => b.Content is "复制");
            using (var child = LockClipboard(2400))
            {
                try
                {
                    var click = window.Dispatcher.InvokeAsync(() => copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                    PumpUntil(() => click.Task.IsCompleted, "复制按钮事件未执行");
                    click.Task.GetAwaiter().GetResult();
                    Check(window.IsVisible && !copy.IsEnabled && copy.Content is "复制中…", "复制等待未保留窗口或未禁用重复点击");
                    var responsive = false;
                    window.Dispatcher.BeginInvoke(() => responsive = true);
                    PumpUntil(() => copy.IsEnabled, "占用失败后没有允许重试");
                    Check(responsive && window.IsVisible && copy.Content is "重试复制", "复制失败后预览丢失或界面被阻塞");
                    Check(Descendants<TextBlock>(window).Any(t => t.Text.Contains("复制失败") && t.Text.Contains("重试")),
                        "复制失败没有可操作的提示");
                    SavePreview(window);
                    ThemeService.ApplyTheme(AppTheme.Light);
                    window.Width = 360;
                    window.UpdateLayout();
                    SavePreview(window, "copy-retry-light-narrow.png");
                    foreach (var button in Descendants<Button>(window))
                    {
                        var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                        Check(bounds.Right <= window.ActualWidth && bounds.Bottom <= window.ActualHeight,
                            "窄窗口复制失败后按钮溢出");
                    }
                }
                finally { Release(child); }
            }
            window.Dispatcher.InvokeAsync(() => copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            PumpUntil(() => !window.IsVisible, "重试复制成功后未关闭预览");
            sequence = Win32.GetClipboardSequenceNumber();
            ReadBack(image);
        }
        finally
        {
            window?.Close();
            ToastWindow.Shutdown();
            settings.LongShotPreviewWidth = width;
            settings.LongShotPreviewHeight = height;
            SettingsService.Instance.Save();
            ThemeService.Apply(settings);
            original.Restore(sequence);
        }
    }

    static void NormalCopyFailure()
    {
        using var original = ClipboardSnapshot.Capture();
        var sequence = original.Sequence;
        var settings = SettingsService.Instance.Current;
        var width = settings.LongShotPreviewWidth;
        var height = settings.LongShotPreviewHeight;
        LongShotWindow? window = null;
        try
        {
            var image = Image();
            using (var child = LockClipboard(2400))
            {
                try
                {
                    Task? handling = null;
                    Application.Current.Dispatcher.InvokeAsync(() => handling = (Task)typeof(AppHost)
                        .GetMethod("HandleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(new AppHost(), [CaptureAction.Copy, image, null])!);
                    PumpUntil(() => handling?.IsCompleted == true, "普通截图复制失败后未收尾");
                    handling!.GetAwaiter().GetResult();
                    window = Application.Current.Windows.OfType<LongShotWindow>().Single();
                    Check(window.IsVisible && Descendants<TextBlock>(window).Any(t => t.Text.Contains("复制未完成")),
                        "普通截图失败后未保留图片或缺少说明");
                }
                finally { Release(child); }
            }
            var copy = Descendants<Button>(window!).Single(b => b.Content is "复制");
            window!.Dispatcher.InvokeAsync(() => copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            PumpUntil(() => !window.IsVisible, "普通截图恢复预览后重试失败");
            sequence = Win32.GetClipboardSequenceNumber();
            ReadBack(image);
        }
        finally
        {
            window?.Close();
            ToastWindow.Shutdown();
            settings.LongShotPreviewWidth = width;
            settings.LongShotPreviewHeight = height;
            SettingsService.Instance.Save();
            original.Restore(sequence);
        }
    }

    static void ReadBack(CapturedImage expected)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var bitmap = Clipboard.GetImage() ?? throw new InvalidOperationException("剪贴板没有图片");
                Check(bitmap.PixelWidth == expected.Width && bitmap.PixelHeight == expected.Height, "剪贴板长图尺寸错误");
                var pixels = new byte[expected.Pixels.Length];
                new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, expected.Stride, 0);
                Check(pixels.AsSpan().SequenceEqual(expected.Pixels), "剪贴板长图像素丢失或改变");
                return;
            }
            catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x800401D0 && attempt < 20)
            { Thread.Sleep(25); }
        }
    }

    static void SavePreview(Window window, string name = "copy-retry.png")
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--preview-dir");
        if (index < 0 || index + 1 >= args.Length) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(System.IO.Path.Combine(args[index + 1], name));
        encoder.Save(stream);
    }

    static void PumpUntil(Func<bool> done, string message)
    {
        var until = Environment.TickCount64 + 6000;
        while (!done() && Environment.TickCount64 < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        Check(done(), message);
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
