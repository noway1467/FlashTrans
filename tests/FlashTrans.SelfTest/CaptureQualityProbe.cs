using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashTrans.Interop;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

/// <summary>高频像素图可检出尺寸正确、但细字已被插值抹掉的情况。</summary>
static class CaptureQualityProbe
{
    public static void RunAll(Action<string, Action> step)
    {
        step("截图画质：多 DPI 裁切与屏幕选区边界一致", CropProbe);
        step("截图画质：标注不能改变未覆盖区域的原始像素", AnnotationProbe);
        step("截图画质：100%–200% 预览保留单像素细节", PreviewProbe);
        step("截图画质：非整数选区的马赛克与原图像素对齐", MosaicAlignmentProbe);
        step("截图画质：PNG 保存逐像素无损", PngProbe);
        step("截图复制：多 DPI、标注及图片加文字逐像素往返", ClipboardExportProbe);
        step("截图画质：钉住预览保持比例且不放大小图", PinProbe);
        step("截图画质：真实屏幕采集、选区预览及钉住逐像素回读", ScreenRoundTripProbe);
    }

    static CapturedImage Pattern(int width = 512, int height = 256)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(x % 2 == 0 ? 0 : 255);
                pixels[i + 1] = (byte)(y % 2 == 0 ? 0 : 255);
                pixels[i + 2] = (byte)((x * 37 + y * 17) % 256);
                pixels[i + 3] = 255;
            }
        return new CapturedImage(width, height, pixels);
    }

    static void CropProbe()
    {
        var shot = Pattern();
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            var selection = new Rect(13.4 / scale, 9.4 / scale, 220.4 / scale, 130.4 / scale);
            var layer = (CaptureSelectionLayer)CaptureOverlay.LayerForShot(shot,
                new Size(shot.Width / scale, shot.Height / scale), selection);
            var bounds = ScreenCapture.ToPixels(selection, scale, scale);
            var expected = CaptureOverlay.CropPixels(shot, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)!;
            Equal(expected, layer.Export()!, $"{scale:P0} 裁切");
        }
    }

    static void AnnotationProbe()
    {
        foreach (var width in new[] { 512, 5003 })
        {
            var shot = Pattern(width, 128);
            var layer = (CaptureSelectionLayer)CaptureOverlay.LayerForShot(shot,
                new Size(width / 1.25, 128 / 1.25), new Rect(0, 0, width / 1.25, 128 / 1.25));
            layer.AddAnnotation(new RectAnnotation
            {
                Bounds = new Rect(200, 20, 40, 40), Color = Colors.Red, Width = 3,
            });
            var actual = layer.Export()!;
            Equal(CaptureOverlay.CropPixels(shot, 0, 0, 180, 128)!,
                CaptureOverlay.CropPixels(actual, 0, 0, 180, 128)!, $"{width}px 标注底图");
            if (actual.Pixels.AsSpan().SequenceEqual(shot.Pixels))
                throw new InvalidOperationException("标注没有画入导出图片");
        }
    }

    static void PreviewProbe()
    {
        var shot = Pattern();
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            var size = new Size(shot.Width / scale, shot.Height / scale);
            var layer = CaptureOverlay.LayerForShot(shot, size, new Rect(size), CaptureTool.Rect);
            var bitmap = new RenderTargetBitmap(shot.Width, shot.Height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(layer);
            // 避开选区边线和标签，只比较未标注的内容。
            Equal(CaptureOverlay.CropPixels(shot, 210, 80, 260, 140)!,
                CaptureOverlay.CropPixels(FromBitmap(bitmap), 210, 80, 260, 140)!, $"{scale:P0} 预览");
        }
    }

    static void MosaicAlignmentProbe()
    {
        var shot = Pattern();
        const double scale = 1.25;
        var selection = new Rect(13.4 / scale, 9.4 / scale, 220 / scale, 130 / scale);
        var layer = (CaptureSelectionLayer)CaptureOverlay.LayerForShot(shot,
            new Size(shot.Width / scale, shot.Height / scale), selection);
        layer.MosaicBlock = 8;
        layer.AddAnnotation(new MosaicAnnotation { Bounds = new Rect(0, 0, shot.Width / scale, shot.Height / scale) });
        Equal(CaptureOverlay.CropPixels(shot.Mosaic(8), 13, 9, 220, 130)!, layer.Export()!, "马赛克像素对齐");
    }

    static void PngProbe()
    {
        var path = Path.Combine(Path.GetTempPath(), $"FlashTrans-quality-{Guid.NewGuid():N}.png");
        try
        {
            var shot = Pattern();
            shot.SavePng(path);
            using var stream = File.OpenRead(path);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Equal(shot, FromBitmap(decoder.Frames[0]), "PNG 往返");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    static void ClipboardExportProbe()
    {
        var shot = Pattern();
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            var layer = (CaptureSelectionLayer)CaptureOverlay.LayerForShot(shot,
                new Size(shot.Width / scale, shot.Height / scale),
                new Rect(13.4 / scale, 9.4 / scale, 220.4 / scale, 130.4 / scale));
            ClipboardRoundTrip(layer.Export()!, $"{scale:P0} 无标注截图");
            layer.AddAnnotation(new RectAnnotation
            {
                Bounds = new Rect(40 / scale, 30 / scale, 80 / scale, 50 / scale),
                Color = Colors.Red, Width = 3,
            });
            ClipboardRoundTrip(layer.Export()!, $"{scale:P0} 带标注截图");
        }
        Console.WriteLine("       5 档 DPI × 无标注/有标注：图片、图片+文字及再次复制图片均保持像素一致");
    }

    static void ClipboardRoundTrip(CapturedImage image, string context)
    {
        var original = Clipboard.GetDataObject();
        try
        {
            void ReadBack()
            {
                if (Clipboard.GetDataObject()?.GetDataPresent(DataFormats.Dib) != true)
                    throw new InvalidOperationException(context + "：未写入标准 DIB 图片");
                var bitmap = Clipboard.GetImage()
                    ?? throw new InvalidOperationException(context + "：剪贴板读不到图片");
                Equal(image, FromBitmap(bitmap), context + " 剪贴板往返");
            }

            SelectionReader.SetImage(image);
            ReadBack();
            const string text = "截图复制回归 Clipboard 123";
            SelectionReader.SetImageAndText(image, text);
            ReadBack();
            if (SelectionReader.ReadText() != text)
                throw new InvalidOperationException(context + "：图片与文字没有同时保留");
            // 识别文字后再复制普通截图，不能让旧文字抢走粘贴结果。
            SelectionReader.SetImage(image);
            ReadBack();
            if (!string.IsNullOrEmpty(SelectionReader.ReadText()))
                throw new InvalidOperationException(context + "：纯图片复制残留了旧文字");
        }
        finally
        {
            if (original is null) Clipboard.Clear();
            else Clipboard.SetDataObject(original, copy: true);
        }
    }

    static void PinProbe()
    {
        foreach (var shot in new[] { Pattern(96, 48), Pattern(1200, 2400), Pattern(2400, 320) })
        {
            var window = new PinnedShotWindow(shot) { ShowActivated = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                Pump();
                var dpi = VisualTreeHelper.GetDpi(window);
                var width = window.ActualWidth * dpi.DpiScaleX;
                var height = window.ActualHeight * dpi.DpiScaleY;
                // 窗口边长必须取整数像素；允许末端不足一像素，不能允许独立拉伸宽高。
                if (Math.Abs(height - width * shot.Height / shot.Width) > 1
                    && Math.Abs(width - height * shot.Width / shot.Height) > 1)
                    throw new InvalidOperationException($"{shot.Width}×{shot.Height} 被拉伸成 {width:F1}×{height:F1}");
                if (width > shot.Width + 1 || height > shot.Height + 1)
                    throw new InvalidOperationException("图片被无故放大");
                var area = ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos(), window);
                if (width > Math.Min(680, area.Width * 0.8) * dpi.DpiScaleX + 1
                    || height > Math.Min(720, area.Height * 0.8) * dpi.DpiScaleY + 1)
                    throw new InvalidOperationException("长图没有缩小到可见范围");
                var picture = ((Grid)window.Content).Children.OfType<Image>().Single();
                if (picture.Stretch != Stretch.Uniform)
                    throw new InvalidOperationException("图片没有保持原始宽高比");
                var expectedMode = width >= shot.Width - 1 && height >= shot.Height - 1
                    ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
                if (RenderOptions.GetBitmapScalingMode(picture) != expectedMode)
                    throw new InvalidOperationException("原尺寸/缩小预览没有使用对应的采样方式");
            }
            finally { window.Close(); Pump(); }
        }
    }

    static void ScreenRoundTripProbe()
    {
        var shot = Pattern();
        var picture = new Image { Source = shot.ToBitmap(), Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
        var area = ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos());
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Left = area.Left + 30, Top = area.Top + 50, Width = 512, Height = 256,
            Content = picture,
        };
        PinnedShotWindow? pin = null;
        try
        {
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            if (!Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, shot.Width, shot.Height,
                    Win32.SWP_NOMOVE | Win32.SWP_NOACTIVATE))
                throw new InvalidOperationException("无法设置测试窗口像素大小");
            Settle();
            if (!Win32.GetWindowRect(hwnd, out var region))
                throw new InvalidOperationException("无法读取测试窗口位置");
            var captured = ScreenCapture.Grab(region) ?? throw new InvalidOperationException("真实屏幕采集失败");
            Equal(shot, captured, "真实屏幕采集");
            var dpi = VisualTreeHelper.GetDpi(window);
            Console.WriteLine($"       桌面 DPI={dpi.PixelsPerInchX:F0}，{shot.Width}×{shot.Height} 单像素细线采集一致");

            var size = new Size(window.ActualWidth, window.ActualHeight);
            var copyLayer = (CaptureSelectionLayer)CaptureOverlay.LayerForShot(captured, size,
                new Rect(13.4 / dpi.DpiScaleX, 9.4 / dpi.DpiScaleY,
                    220.4 / dpi.DpiScaleX, 130.4 / dpi.DpiScaleY));
            ClipboardRoundTrip(copyLayer.Export()!, "真实截图裁切复制");
            Console.WriteLine("       真实截图裁切后复制：尺寸、所有像素及文字格式往返一致");
            window.Content = CaptureOverlay.LayerForShot(captured, size, new Rect(size), CaptureTool.Rect);
            Settle();
            Equal(CaptureOverlay.CropPixels(captured, 210, 80, 260, 140)!,
                CaptureOverlay.CropPixels(ScreenCapture.Grab(region)!, 210, 80, 260, 140)!, "真实选区预览");

            // 即使调用方区域多一像素，也不能把底图伸展过去。
            var mismatched = region;
            mismatched.Right++;
            mismatched.Bottom++;
            pin = new PinnedShotWindow(captured, mismatched) { ShowActivated = false };
            pin.Show();
            Settle();
            pin.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = UIElement.MouseLeaveEvent });
            Settle();
            if (!Win32.GetWindowRect(new WindowInteropHelper(pin).Handle, out var pinnedRegion)
                || pinnedRegion.Left != region.Left || pinnedRegion.Top != region.Top
                || pinnedRegion.Right != region.Right || pinnedRegion.Bottom != region.Bottom)
                throw new InvalidOperationException("钉住窗口没有按原图物理像素定位");
            Equal(captured, ScreenCapture.Grab(pinnedRegion)!, "真实钉住预览");
        }
        finally { pin?.Close(); window.Close(); Pump(); }
    }

    static CapturedImage FromBitmap(BitmapSource bitmap)
    {
        var source = bitmap.Format == PixelFormats.Bgra32 ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return new CapturedImage(source.PixelWidth, source.PixelHeight, pixels);
    }

    static void Equal(CapturedImage expected, CapturedImage actual, string context)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height)
            throw new InvalidOperationException($"{context}：应为 {expected.Width}×{expected.Height}，实际 {actual.Width}×{actual.Height}");
        var different = 0;
        for (var i = 0; i < expected.Pixels.Length; i += 4)
            if (!expected.Pixels.AsSpan(i, 4).SequenceEqual(actual.Pixels.AsSpan(i, 4))) different++;
        if (different != 0)
            throw new InvalidOperationException($"{context}：{different}/{expected.Width * expected.Height} 个像素发生变化");
    }

    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    static void Settle()
    {
        Pump();
        Thread.Sleep(250);
        Pump();
    }
}
