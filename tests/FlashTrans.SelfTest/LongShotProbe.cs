using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

/// <summary>
/// 长截图的拼接算法。这部分不碰屏幕也不用滚鼠标，纯粹是「两帧图对齐了没有」，
/// 所以能直接造图来验——真去滚一遍屏幕的话，滚多少、什么时候停都不由自己说了算，
/// 边界情况一个都测不到。
/// </summary>
static class LongShotProbe
{
    public static void RunAll(Action<string, Action> step)
    {
        step("长截图：固定底栏不会被重复拼进长图", FixedBottomProbe);
        step("长截图：普通底部留白不会被误裁", FixedBottomFalsePositiveProbe);
        step("长截图：能算出滚了多少像素", ShiftProbe);
        step("长截图：滚到底（画面没变）时算出 0", BottomProbe);
        step("长截图：画面整体换了内容时拒绝拼接", DivergeProbe);
        step("长截图：留白处不会被当成滚到底", FlatBandProbe);
        step("长截图：接起来的长图行数和内容都对", StackProbe);
        step("长截图：固定浮层不会被误判成滚到底", FixedOverlayProbe);
        step("长截图：滚动距离超过初始窄带也能对齐", LargeShiftProbe);
        step("长截图：动态刷新和回弹期间不反向滚、不拼半帧", DynamicRefreshProbe);
        step("长截图：滚动事件延迟时不会提前判定到底", DelayedScrollProbe);
        step("长截图：100% 预览在不同 DPI 下保持物理像素一比一", PreviewDpiProbe);
        step("长截图：超长图分片虚拟化，缩放和滚到底后仍保留原像素", PreviewTilesProbe);
        step("长截图：无损 PNG 往返不缩图、不改细线像素", LosslessExportProbe);
        step("长截图：真实 WPF 滚动、GDI 抓屏和拼接保持原像素", RealScrollPixelsProbe);
    }

    // ------------------------------------------------------------- 造图

    const int W = 240;
    const int H = 300;

    /// <summary>
    /// 一张够高的「网页」：每行的颜色由行号决定，相邻行差得够开，
    /// 这样位移多少一比就知道。
    /// 差值要明显超过对齐时那 8 个色阶的容差——差太少的话，挪一行和没挪
    /// 在算法看来就是一回事，那测的是造图手法而不是拼接逻辑。
    /// </summary>
    static CapturedImage Page(int height)
    {
        var buf = new byte[W * 4 * height];
        for (var y = 0; y < height; y++)
        {
            // 每行一个散开的伪随机色。用等差色阶行不通：取模总会让相隔某个固定行数的
            // 两行差在容差以内（绕回来了），那测出来的是造图的毛病，不是对齐的毛病。
            var h = RowColor((uint)y);
            var b = (byte)(h >> 24);
            var g = (byte)(h >> 16);
            var r = (byte)(h >> 8);
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                buf[i] = b;
                buf[i + 1] = g;
                // 横向带点花纹，免得整行同色
                buf[i + 2] = (byte)(r ^ (x * 5 % 32));
                buf[i + 3] = 0xFF;
            }
        }
        return new CapturedImage(W, height, buf);
    }

    /// <summary>行号打散成一个颜色。同一行每次都得到同一个色，图才是可复现的。</summary>
    static uint RowColor(uint y)
    {
        var h = y * 2654435761;      // Knuth 的乘法散列
        h ^= h >> 13;
        h *= 1274126177;
        h ^= h >> 16;
        return h;
    }

    /// <summary>从长页面里截一屏，模拟滚到 offset 处看到的画面。</summary>
    static CapturedImage Frame(CapturedImage page, int offset)
        => LongShotService.Crop(page, offset, H)
           ?? throw new InvalidOperationException($"截不出第 {offset} 行起的一屏");

    /// <summary>整片纯色，用来试「窄带没有内容」的情况。</summary>
    static CapturedImage Flat(int height, byte v)
    {
        var buf = new byte[W * 4 * height];
        for (var i = 0; i < buf.Length; i += 4)
        {
            buf[i] = buf[i + 1] = buf[i + 2] = v;
            buf[i + 3] = 0xFF;
        }
        return new CapturedImage(W, height, buf);
    }

    // ------------------------------------------------------------- 用例

    static void ShiftProbe()
    {
        var page = Page(1200);
        var prev = Frame(page, 0);
        var band = LongShotService.PickBand(prev);

        // 从小到大都试一遍，含刚好等于窄带上限那个边界
        foreach (var moved in new[] { 1, 17, 120, band - 1, band })
        {
            var next = Frame(page, moved);
            var got = LongShotService.FindShift(prev, next, band);
            if (got != moved)
                throw new InvalidOperationException($"滚了 {moved} 行，算出来是 {got}");
        }
        Console.WriteLine($"       窄带上限 {band} 行，1 到 {band} 都算对了");
    }

    static void BottomProbe()
    {
        var page = Page(1200);
        var frame = Frame(page, 400);
        var band = LongShotService.PickBand(frame);
        // 同一帧比自己：没动，应该是 0，调用方据此判定到底
        var got = LongShotService.FindShift(frame, frame, band);
        if (got != 0) throw new InvalidOperationException($"画面没变却算出滚了 {got} 行");
    }

    static void DivergeProbe()
    {
        var prev = Frame(Page(1200), 0);
        var band = LongShotService.PickBand(prev);

        // 另一张毫不相干的页面：接不上就该说接不上，硬拼会在长图里留一段空缺
        var other = new CapturedImage(W, H, Page(1200).Pixels.Reverse().ToArray());
        var got = LongShotService.FindShift(prev, other, band);
        if (got >= 0) throw new InvalidOperationException($"画面换了内容，却算出滚了 {got} 行");
    }

    static void FlatBandProbe()
    {
        // 底部一大片留白、上面才有内容：窄带要往上挪到有变化的地方去，
        // 不然纯色带在哪儿都能对上，第一个中的就是 0，会被误判成滚到底了。
        var page = Page(1200);
        var tall = LongShotService.Stack([Frame(page, 0), Flat(200, 0xF4)], W)
                   ?? throw new InvalidOperationException("造不出「下方留白」的图");

        var prev = LongShotService.Crop(tall, tall.Height - H, H)!;
        var band = LongShotService.PickBand(prev);
        var bottom = prev.Height - 40;
        if (band >= bottom)
            throw new InvalidOperationException($"窄带停在纯色区（{band}，底部是 {bottom}）");
        Console.WriteLine($"       窄带挪到了第 {band} 行（底部纯色区从 {H - 200} 行开始）");
    }

    static void StackProbe()
    {
        var page = Page(1200);
        var parts = new List<CapturedImage> { Frame(page, 0) };

        // 模拟滚三次，每次只把新露出来的那几行接上——服务里就是这么攒的
        var offsets = new[] { 90, 180, 270 };
        foreach (var off in offsets)
            parts.Add(LongShotService.Crop(Frame(page, off), H - 90, 90)!);

        var joined = LongShotService.Stack(parts, W)
                     ?? throw new InvalidOperationException("拼接返回 null");

        var want = H + 90 * offsets.Length;
        if (joined.Height != want)
            throw new InvalidOperationException($"高度应该是 {want}，实际 {joined.Height}");
        if (joined.Width != W) throw new InvalidOperationException($"宽度变成了 {joined.Width}");

        // 拼出来的应该就是原页面的前 want 行，逐字节比
        for (var i = 0; i < want * W * 4; i++)
            if (joined.Pixels[i] != page.Pixels[i])
                throw new InvalidOperationException(
                    $"第 {i / 4 / W} 行第 {i / 4 % W} 列对不上（拼接结果和原图不一致）");

        Console.WriteLine($"       {parts.Count} 段接成 {joined.Width}x{joined.Height}，逐像素与原页面一致");
    }

    static void FixedOverlayProbe()
    {
        var page = Page(1200);
        var prev = Frame(page, 0);
        var scrolled = Frame(page, 90);
        const int overlayHeight = 60;
        var pixels = (byte[])scrolled.Pixels.Clone();
        Array.Copy(prev.Pixels, (H - overlayHeight) * prev.Stride,
            pixels, (H - overlayHeight) * prev.Stride, overlayHeight * prev.Stride);
        var next = new CapturedImage(W, H, pixels);
        var got = LongShotService.FindShift(prev, next, LongShotService.PickBand(prev));
        if (got != 90)
            throw new InvalidOperationException($"固定浮层遮住底部时应算出 90，实际是 {got}");
    }

    static void LargeShiftProbe()
    {
        var page = Page(1200);
        var prev = Frame(page, 0);
        var next = Frame(page, 150);
        var got = LongShotService.FindShift(prev, next, bandTop: 80);
        if (got != 150)
            throw new InvalidOperationException($"滚了 150 行、初始窄带在 80 行时应算出 150，实际是 {got}");
    }

    static void DynamicRefreshProbe()
    {
        var page = Page(1200);
        var scrolls = 0;
        var samples = 0;
        var wheelDeltas = new List<int>();

        CapturedImage Capture(RECT _)
        {
            if (scrolls == 0) return Frame(page, 0);

            samples++;
            var previousOffset = (scrolls - 1) * 90;
            var targetOffset = Math.Min(scrolls * 90, 270);

            if (samples <= 2) return Frame(page, previousOffset);
            if (scrolls == 1 && samples is >= 3 and <= 8)
                return PartialFrame(page, targetOffset, 55);
            if (scrolls == 2 && samples == 3)
                return Frame(page, previousOffset - 30);
            if (scrolls >= 4) return Frame(page, 270);
            return Frame(page, targetOffset);
        }

        void Scroll(int delta)
        {
            wheelDeltas.Add(delta);
            scrolls++;
            samples = 0;
        }

        var result = Task.Run(() => LongShotService.RunForTestAsync(
                new RECT { Left = 0, Top = 0, Right = W, Bottom = H },
                Capture, Scroll, delay: _ => Task.CompletedTask))
            .GetAwaiter().GetResult();

        if (result.Image is null) throw new InvalidOperationException("动态页面没有拼出结果");
        if (result.Stopped != LongShotStop.Bottom)
            throw new InvalidOperationException($"动态页面应稳定滚到底，实际 {result.Stopped}");
        if (result.Frames != 4)
            throw new InvalidOperationException($"应接到首屏加 3 段，实际 {result.Frames} 屏");
        if (wheelDeltas.Count < 4 || wheelDeltas.Any(d => d >= 0))
            throw new InvalidOperationException($"动态页面滚动出现了反向滚轮：{string.Join(",", wheelDeltas)}");

        var want = LongShotService.Crop(page, 0, H + 90 * 3)!;
        if (!result.Image.Pixels.AsSpan().SequenceEqual(want.Pixels))
            throw new InvalidOperationException("刷新中间帧被拼进结果，页面出现半帧或缺口");
    }

    static void DelayedScrollProbe()
    {
        var page = Page(1200);
        var acceptedOffset = 0;
        var targetOffset = 0;
        var samples = 0;
        var wheelDeltas = new List<int>();

        CapturedImage Capture(RECT _)
        {
            if (targetOffset == acceptedOffset) return Frame(page, acceptedOffset);
            samples++;
            return samples <= 14
                ? Frame(page, acceptedOffset)
                : Frame(page, targetOffset);
        }

        void Scroll(int delta)
        {
            wheelDeltas.Add(delta);
            if (targetOffset == acceptedOffset && acceptedOffset < 270)
            {
                targetOffset += 90;
                samples = 0;
            }
        }

        var result = Task.Run(() => LongShotService.RunForTestAsync(
                new RECT { Left = 0, Top = 0, Right = W, Bottom = H },
                Capture, Scroll,
                onProgress: (height, _) => acceptedOffset = height - H,
                delay: _ => Task.CompletedTask))
            .GetAwaiter().GetResult();

        if (result.Image is null || result.Stopped != LongShotStop.Bottom)
            throw new InvalidOperationException($"延迟滚动未完整结束：{result.Stopped}");
        if (result.Frames != 4)
            throw new InvalidOperationException($"延迟滚动应接到 4 屏，实际 {result.Frames}");
        if (wheelDeltas.Count < 4 || wheelDeltas.Any(d => d >= 0))
            throw new InvalidOperationException("延迟滚动出现了反向滚轮");

        var want = LongShotService.Crop(page, 0, H + 90 * 3)!;
        if (!result.Image.Pixels.AsSpan().SequenceEqual(want.Pixels))
            throw new InvalidOperationException("滚动事件延迟导致长图提前截断或缺少内容");
    }

    static CapturedImage PartialFrame(CapturedImage page, int offset, int loadingRows)
    {
        var frame = Frame(page, offset);
        var pixels = (byte[])frame.Pixels.Clone();
        var top = Math.Max(0, frame.Height - loadingRows);
        for (var y = top; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var i = y * frame.Stride + x * 4;
                pixels[i] = 0xEE;
                pixels[i + 1] = 0xEE;
                pixels[i + 2] = 0xEE;
                pixels[i + 3] = 0xFF;
            }
        return new CapturedImage(frame.Width, frame.Height, pixels);
    }

    static void FixedBottomProbe()
    {
        var page = Page(1200);
        const int moved = 90;
        const int fixedHeight = 12;
        var prev = WithFixedBottom(Frame(page, 0), fixedHeight);
        var next = WithFixedBottom(Frame(page, moved), fixedHeight);
        var shift = LongShotService.FindShift(prev, next, LongShotService.PickBand(prev));
        var fixedBottom = LongShotService.FindFixedBottom(prev, next, shift);
        if (shift != moved || fixedBottom != fixedHeight)
            throw new InvalidOperationException(
                $"位移/固定底栏应为 {moved}/{fixedHeight}，实际 {shift}/{fixedBottom}");

        var first = LongShotService.Crop(prev, 0, H - fixedBottom)!;
        var fresh = LongShotService.Crop(next, H - fixedBottom - shift, shift)!;
        var joined = LongShotService.Stack([first, fresh], W)!;
        var want = H - fixedHeight + moved;
        if (joined.Height != want)
            throw new InvalidOperationException($"拼接高度应为 {want}，实际 {joined.Height}");
        for (var i = 0; i < want * W * 4; i++)
            if (joined.Pixels[i] != page.Pixels[i])
                throw new InvalidOperationException($"第 {i / 4 / W} 行拼接不连续");
    }

    static CapturedImage WithFixedBottom(CapturedImage frame, int height)
    {
        var pixels = (byte[])frame.Pixels.Clone();
        for (var y = frame.Height - height; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var i = y * frame.Stride + x * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0x20;
                pixels[i + 3] = 0xFF;
            }
        return new CapturedImage(frame.Width, frame.Height, pixels);
    }

    static void FixedBottomFalsePositiveProbe()
    {
        var page = Page(1200);
        var prev = Frame(page, 0);
        var next = Frame(page, 90);
        var pixels = (byte[])next.Pixels.Clone();
        Array.Copy(prev.Pixels, (H - 1) * prev.Stride, pixels, (H - 1) * prev.Stride, prev.Stride);
        next = new CapturedImage(W, H, pixels);

        var fixedBottom = LongShotService.FindFixedBottom(prev, next, 90);
        if (fixedBottom != 0)
            throw new InvalidOperationException($"单行巧合被误裁成 {fixedBottom} 行固定底栏");
    }

    static void PreviewDpiProbe()
    {
        foreach (var height in new[] { 1, 1023, 1024, 1025, 40017 })
        {
            var total = 0;
            for (var index = 0; index < (height + LongShotWindow.PreviewTileHeight - 1) / LongShotWindow.PreviewTileHeight; index++)
            {
                var rows = LongShotWindow.PreviewTileRows(height, index);
                Check(rows.Top == total && rows.Height is > 0 and <= LongShotWindow.PreviewTileHeight, "分片行范围缺失、重叠或超过纹理上限");
                total += rows.Height;
            }
            Check(total == height, "分片没有覆盖完整原图");
        }
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var dpi = new DpiScale(scale, scale);
            var size = LongShotWindow.PreviewSize(1921, 1024, 1024, 1, dpi);
            Check(Math.Abs(size.Width * scale - 1921) < 0.001, "100% 预览宽度被 DPI 放大");
            Check(Math.Abs(size.Height * scale - 1024) < 0.001, "100% 预览高度被 DPI 放大");
            var total = Enumerable.Range(0, 31).Sum(index =>
                LongShotWindow.PreviewSize(1921, index * 1024, 1024, 0.37, dpi).Height * scale);
            Check(Math.Abs(total - Math.Round(31 * 1024 * 0.37)) < 0.001, "分片缩放累计出空隙或重复行");
        }
    }

    static CapturedImage DetailedPage(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 4;
                var line = y % 17 == 0 || x % 13 == 0;
                pixels[at] = line ? (byte)0 : (byte)245;
                pixels[at + 1] = (byte)(line ? 0 : 220 + (y / 17 % 30));
                pixels[at + 2] = (byte)(line ? 0 : 220 + (x / 13 % 30));
                pixels[at + 3] = 255;
            }
        return new CapturedImage(width, height, pixels);
    }

    static void PreviewTilesProbe()
    {
        var page = DetailedPage(513, 40017);
        var window = new LongShotWindow(page, "原始像素测试")
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000, Top = -4000, Width = 420, Height = 480,
            ShowInTaskbar = false, ShowActivated = false,
        };
        try
        {
            window.Show(); Layout(window);
            var list = Descendants<ListBox>(window).Single(l => l.Name == "LongShotPreview");
            var scroll = Descendants<ScrollViewer>(list).First();
            var dpi = VisualTreeHelper.GetDpi(window);

            void VerifyTiles()
            {
                var images = Descendants<Image>(list).Where(i => i.Source is BitmapSource b && b.PixelWidth == page.Width).ToList();
                Check(images.Count > 0 && images.Count < list.Items.Count, "预览未分片或一次性创建了整张超长图");
                foreach (var image in images)
                {
                    var bitmap = (BitmapSource)image.Source;
                    var container = ItemsControl.ContainerFromElement(list, image);
                    var index = list.ItemContainerGenerator.IndexFromContainer(container);
                    Check(index >= 0 && bitmap.PixelHeight <= LongShotWindow.PreviewTileHeight, "分片仍超出纹理安全高度");
                    var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(bytes, page.Stride, 0);
                    var top = LongShotWindow.PreviewTileRows(page.Height, index).Top;
                    Check(bytes.AsSpan().SequenceEqual(page.Pixels.AsSpan(top * page.Stride, bytes.Length)),
                        "预览分片改变了原图像素或行序");
                }
            }

            VerifyTiles();
            Check(scroll.ExtentWidth > scroll.ViewportWidth, "原始大小预览没有提供横向滚动");
            foreach (var image in Descendants<Image>(list).Where(i => i.Source is BitmapSource b && b.PixelWidth == page.Width))
                Check(Math.Abs(image.ActualWidth * dpi.DpiScaleX - page.Width) <= 1, "实际布局不是原像素 100% 显示");
            scroll.ScrollToEnd(); Layout(window); VerifyTiles();
            Check(scroll.VerticalOffset > 30000 / dpi.DpiScaleY,
                $"无法滚到原图底部：offset={scroll.VerticalOffset:0.##}, extent={scroll.ExtentHeight:0.##}, viewport={scroll.ViewportHeight:0.##}, dpi={dpi.DpiScaleY:0.##}");
            window.SetPreviewZoom(0.5); Layout(window); VerifyTiles();
            var fit = Descendants<Button>(window).Single(b => b.Content is "适应宽度");
            fit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window); VerifyTiles();
            Check(scroll.ExtentWidth <= scroll.ViewportWidth + 2, "适应宽度后仍被横向裁切");
            window.Width = 380; Layout(window); VerifyTiles();
            Check(scroll.ExtentWidth <= scroll.ViewportWidth + 2, "调整窗口宽度后没有重新适应");
            window.SetPreviewZoom(1); Layout(window); VerifyTiles();

            var args = Environment.GetCommandLineArgs();
            var preview = Array.IndexOf(args, "--preview-dir");
            if (preview >= 0 && preview + 1 < args.Length)
            {
                Directory.CreateDirectory(args[preview + 1]);
                scroll.ScrollToTop(); Layout(window);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(args[preview + 1], "longshot-preview.png"));
                encoder.Save(output);
            }
        }
        finally { window.Close(); }
    }

    static void LosslessExportProbe()
    {
        var dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FlashTrans.longshot." + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(dir);
        try
        {
            var page = DetailedPage(257, 17001);
            var path = Path.Combine(dir, "detail.png");
            page.SavePng(path);
            var decoded = AnimEncoder.LoadFrame(path);
            Check(decoded.PixelWidth == page.Width && decoded.PixelHeight == page.Height, "PNG 导出改变了原始尺寸");
            var pixels = new byte[page.Pixels.Length];
            new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, page.Stride, 0);
            Check(pixels.AsSpan().SequenceEqual(page.Pixels), "PNG 导出改写了细线、文字边缘或颜色");
        }
        finally
        {
            if (!dir.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("临时路径越界");
            Directory.Delete(dir, recursive: true);
        }
    }

    static void RealScrollPixelsProbe()
    {
        var page = Page(900);
        var picture = new Image { Source = page.ToBitmap(), Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
        var scroll = new ScrollViewer
        {
            Content = picture, CanContentScroll = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };
        var border = new Border { Background = Brushes.White, Child = scroll };
        var window = new Window
        {
            Content = border, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            UseLayoutRounding = true, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 80, Top = 80, Width = 300, Height = 360,
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            window.Show(); Layout(window);
            var dpi = VisualTreeHelper.GetDpi(window);
            picture.Width = page.Width / dpi.DpiScaleX;
            picture.Height = page.Height / dpi.DpiScaleY;
            scroll.Width = W / dpi.DpiScaleX;
            scroll.Height = H / dpi.DpiScaleY;
            border.Padding = new Thickness(12 / dpi.DpiScaleX, 12 / dpi.DpiScaleY, 12 / dpi.DpiScaleX, 12 / dpi.DpiScaleY);
            window.Width = (W + 24) / dpi.DpiScaleX;
            window.Height = (H + 24) / dpi.DpiScaleY;
            Layout(window);
            var origin = scroll.PointToScreen(new Point(0, 0));
            var region = new RECT { Left = (int)Math.Round(origin.X), Top = (int)Math.Round(origin.Y) };
            region.Right = region.Left + W;
            region.Bottom = region.Top + H;
            using var capture = ScreenCaptureSession.Open(region.Left, region.Top, W, H)
                ?? throw new InvalidOperationException("真实滚动自测无法创建抓屏会话");
            var offset = 0;
            var task = Task.Run(() => LongShotService.RunForTestAsync(region, _ => capture.Grab(),
                _ => window.Dispatcher.Invoke(() => scroll.ScrollToVerticalOffset((offset = Math.Min(600, offset + 90)) / dpi.DpiScaleY)),
                cancelled: () => stop.IsCancellationRequested,
                delay: _ => Task.Delay(15)));
            while (!task.IsCompleted) { Layout(window); Thread.Sleep(5); }
            var result = task.GetAwaiter().GetResult();
            Check(result.Stopped == LongShotStop.Bottom && result.Image is not null && result.Frames > 1,
                $"真实滚动未完成：{result.Stopped}，{result.Frames} 屏");
            Check(result.Image!.Width == page.Width && result.Image.Height == page.Height, "真实滚动拼接丢失了原始尺寸");
            Check(result.Image.Pixels.AsSpan().SequenceEqual(page.Pixels), "真实抓屏或拼接发生缩放、模糊或像素错位");
        }
        finally { stop.Cancel(); window.Close(); }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void Layout(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            window.UpdateLayout();
        }
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
