using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        step("长截图：到底后的局部动画不把重复纹理当成继续滚动", BottomAnimationProbe);
        step("长截图：手动等待刷新后再拼接，不留下透明横条和半帧", ManualRefreshProbe);
        step("长截图：持续变化的尾部不会被强行拼接", AnimatedTailProbe);
        step("长截图：透明底栏和中途出现的底栏不重复入图", TranslucentFooterProbe);
        step("长截图：滚动事件延迟时不会提前判定到底", DelayedScrollProbe);
        step("长截图：手动停顿不结束，连续滚动逐像素拼接且不发送滚轮", ManualScrollProbe);
        step("长截图：手动回滚和过快滚动可恢复，不重复拼接或跳过内容", ManualRecoveryProbe);
        step("长截图：手动连续小幅滚动不提前触发自动次数上限", ManualSmallStepsProbe);
        step("长截图：自动等待期间切回手动，再开启自动不丢帧", ModeSwitchProbe);
        step("长截图：手动/自动浮条、完成按钮及深浅主题布局", HudProbe);
        step("长截图：100% 预览在不同 DPI 下保持物理像素一比一", PreviewDpiProbe);
        step("长截图：超长图分片虚拟化，缩放和滚到底后仍保留原像素", PreviewTilesProbe);
        step("长截图：无损 PNG 往返不缩图、不改细线像素", LosslessExportProbe);
        step("长截图：真实 WPF 滚动、GDI 抓屏和拼接保持原像素", () => RealScrollPixelsProbe());
        step("长截图：真实 WPF 半透明底部浮层不进入拼接结果", () => RealScrollPixelsProbe(footerHeight: 32));
        step("长截图：真实手动滚轮和拖动滚动条，鼠标位置不被接管", RealManualScrollProbe);
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

    static void BottomAnimationProbe()
    {
        var tile = Page(60);
        var page = LongShotService.Stack(Enumerable.Repeat(tile, 20).ToList(), W)!;
        var previous = Frame(page, 0);
        var next = Frame(page, 0);
        for (var y = H - 18; y < H; y++)
            for (var x = W - 60; x < W; x++)
            {
                var i = y * next.Stride + x * 4;
                next.Pixels[i] = next.Pixels[i + 1] = next.Pixels[i + 2] = 255;
            }
        Check(LongShotService.FindShift(previous, next, LongShotService.PickBand(previous)) == 0,
            "页面未滚动，底部小动画却让重复纹理被识别成滚动");
    }

    static void ManualRefreshProbe()
    {
        var page = Page(1200);
        var ready = false;
        var accepted = 0;
        var samples = 0;
        var captures = 0;
        var result = Task.Run(() => LongShotService.RunForTestAsync(
            new RECT { Right = W, Bottom = H },
            _ =>
            {
                if (!ready) return Frame(page, 0);
                captures++;
                var next = Frame(page, Math.Min(270, accepted + 90));
                // 模拟浏览器下半部尚未完成合成；前几帧带半透明残影，之后才刷新干净。
                return ++samples <= 6 ? WithTranslucentFooter(next, 55) : next;
            }, _ => throw new InvalidOperationException("手动模式发送了滚轮"),
            onProgress: (height, _) => { ready = true; accepted = height - H; samples = 0; },
            cancelled: () => accepted >= 270 || captures >= 160,
            delay: _ => Task.CompletedTask, automatic: () => false)).GetAwaiter().GetResult();
        Check(result.Image is not null && result.Image.Height == H + 270
            && result.Image.Pixels.AsSpan().SequenceEqual(LongShotService.Crop(page, 0, H + 270)!.Pixels),
            "手动拼接保留了刷新中的透明横条或缺少正文");
    }

    static void AnimatedTailProbe()
    {
        foreach (var automatic in new[] { false, true })
        {
            var page = Page(1200);
            var ready = false;
            var samples = 0;
            var result = Task.Run(() => LongShotService.RunForTestAsync(
                new RECT { Right = W, Bottom = H },
                _ =>
                {
                    if (!ready) return Frame(page, 0);
                    var next = Frame(page, 90);
                    return ++samples % 2 == 0 ? WithTranslucentFooter(next, 55) : PartialFrame(page, 90, 55);
                }, _ => { }, onProgress: (_, _) => ready = true,
                cancelled: () => !automatic && samples >= 40,
                delay: _ => Task.CompletedTask, automatic: () => automatic)).GetAwaiter().GetResult();
            Check(result.Frames == 1 && result.Image!.Pixels.AsSpan().SequenceEqual(Frame(page, 0).Pixels),
                $"{(automatic ? "自动" : "手动")}模式把持续变化的尾部强行接进结果");
        }
    }

    static CapturedImage WithTranslucentFooter(CapturedImage frame, int height)
    {
        var pixels = (byte[])frame.Pixels.Clone();
        for (var y = frame.Height - height; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var i = y * frame.Stride + x * 4;
                for (var channel = 0; channel < 3; channel++) pixels[i + channel] = (byte)((pixels[i + channel] + 220) / 2);
            }
        return new CapturedImage(frame.Width, frame.Height, pixels);
    }

    static void TranslucentFooterProbe()
    {
        var page = Page(1200);
        const int footer = 32;
        foreach (var shift in new[] { 5, 12, 90 })
        {
            var previous = WithTranslucentFooter(Frame(page, 0), footer);
            var next = WithTranslucentFooter(Frame(page, shift), footer);
            Check(LongShotService.FindFixedBottom(previous, next, shift) == footer,
                $"位移 {shift} 时无法识别透明底栏");
        }
        foreach (var appearsAt in new[] { 0, 2 })
        {
            var scrolls = 0;
            var result = Task.Run(() => LongShotService.RunForTestAsync(
                new RECT { Right = W, Bottom = H },
                _ =>
                {
                    var frame = Frame(page, Math.Min(270, scrolls * 90));
                    return scrolls >= appearsAt ? WithTranslucentFooter(frame, footer) : frame;
                }, _ => scrolls++, delay: _ => Task.CompletedTask)).GetAwaiter().GetResult();
            var expected = LongShotService.Crop(page, 0, H + 270 - footer)!;
            Check(result.Stopped == LongShotStop.Bottom && result.Image is not null
                && result.Image.Pixels.AsSpan().SequenceEqual(expected.Pixels),
                $"第 {appearsAt} 次滚动出现的透明底栏造成重复、缺行或横条");
        }
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

    static void ManualScrollProbe()
    {
        var page = Page(1200);
        var ready = false;
        var samples = 0;
        var scrolls = 0;
        var result = Task.Run(() => LongShotService.RunForTestAsync(
            new RECT { Right = W, Bottom = H },
            _ => Frame(page, ready ? Math.Clamp(++samples - 120, 0, 12) * 30 : 0),
            _ => scrolls++, onProgress: (_, _) => ready = true,
            cancelled: () => samples >= 160, delay: _ => Task.CompletedTask,
            automatic: () => false)).GetAwaiter().GetResult();
        Check(scrolls == 0, "手动模式发送了模拟滚轮");
        Check(result.Stopped == LongShotStop.Cancelled && samples >= 160,
            "手动停顿或到底被当成自动结束");
        Check(result.Frames == 13 && result.Image is not null
            && result.Image.Pixels.AsSpan().SequenceEqual(LongShotService.Crop(page, 0, H + 360)!.Pixels),
            "连续手动滚动没有及时拼接，导致缺行或重复");
    }

    static void ManualRecoveryProbe()
    {
        var page = Page(1200);
        var ready = false;
        var samples = 0;
        var lost = new List<bool>();
        int[] offsets = [90, 45, 500, 180, 270, 270];
        var result = Task.Run(() => LongShotService.RunForTestAsync(
            new RECT { Right = W, Bottom = H },
            _ => Frame(page, ready ? offsets[Math.Min(samples++, offsets.Length - 1)] : 0),
            _ => throw new InvalidOperationException("手动模式不应发送滚轮"),
            onProgress: (_, _) => ready = true, cancelled: () => samples >= offsets.Length,
            delay: _ => Task.CompletedTask, automatic: () => false, onAlignmentLost: lost.Add))
            .GetAwaiter().GetResult();
        Check(lost.Contains(true) && !lost.Last(), "接不上时没有提示，或恢复后提示未消失");
        Check(result.Stopped == LongShotStop.Cancelled && result.Frames == 4
            && result.Image!.Pixels.AsSpan().SequenceEqual(LongShotService.Crop(page, 0, H + 270)!.Pixels),
            "回滚或滚动过快后丢失了原有拼接基准");
    }

    static void ModeSwitchProbe()
    {
        var page = Page(1200);
        var automatic = true;
        var scrolls = 0;
        var finished = false;
        var result = Task.Run(() => LongShotService.RunForTestAsync(
            new RECT { Right = W, Bottom = H }, _ => Frame(page, scrolls * 90), _ => scrolls++,
            onProgress: (_, frames) =>
            {
                if (frames == 2) automatic = true;
                if (frames == 3) finished = true;
            },
            cancelled: () => finished,
            delay: _ => { if (scrolls == 1) automatic = false; return Task.CompletedTask; },
            automatic: () => automatic)).GetAwaiter().GetResult();
        Check(result.Stopped == LongShotStop.Cancelled && scrolls == 2 && result.Frames == 3
            && result.Image!.Pixels.AsSpan().SequenceEqual(LongShotService.Crop(page, 0, H + 180)!.Pixels),
            "切换滚动模式导致提前结束或丢帧");
    }

    static void ManualSmallStepsProbe()
    {
        var page = Page(1200);
        var ready = false;
        var samples = 0;
        var result = Task.Run(() => LongShotService.RunForTestAsync(
            new RECT { Right = W, Bottom = H },
            _ => Frame(page, ready ? Math.Min(600, ++samples * 2) : 0),
            _ => throw new InvalidOperationException("手动模式不应发送滚轮"),
            onProgress: (_, _) => ready = true, cancelled: () => samples >= 310,
            delay: _ => Task.CompletedTask, automatic: () => false)).GetAwaiter().GetResult();
        Check(result.Stopped == LongShotStop.Cancelled && result.Frames > LongShotService.MaxFrames
            && result.Image!.Pixels.AsSpan().SequenceEqual(LongShotService.Crop(page, 0, H + 600)!.Pixels),
            "手动连续小幅滚动被自动次数上限提前截断");
    }

    static void HudProbe()
    {
        try
        {
            foreach (var theme in new[] { FlashTrans.Core.AppTheme.Dark, FlashTrans.Core.AppTheme.Light })
            {
                ThemeService.ApplyTheme(theme);
                var hud = new LongShotHud(new RECT { Left = 80, Top = 80, Right = 700, Bottom = 500 });
                try
                {
                    hud.Show(); Layout(hud);
                    var mode = Descendants<Button>(hud).Single(b => b.Name == "LongShotMode");
                    Check(!hud.Automatic && !mode.IsEnabled, "初始状态没有等待手动滚动或首帧未准备好就能自动滚动");
                    hud.Report(300, 1); Layout(hud);
                    Check(mode.IsEnabled, "首帧完成后未启用切换");
                    SaveHud(hud, $"longshot-hud-{theme}-manual.png");
                    mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(hud);
                    Check(hud.Automatic && !hud.Cancelled && mode.Content is "手动滚动", "切换自动时误结束或文案不正确");
                    SaveHud(hud, $"longshot-hud-{theme}-auto.png");
                    mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    hud.ReportAlignmentLost(true); Layout(hud);
                    Check(!hud.Automatic && Descendants<TextBlock>(hud).Any(t => t.Text.Contains("回滚")), "手动接不上时缺少操作提示");
                    SaveHud(hud, $"longshot-hud-{theme}-recover.png");
                    foreach (var width in new[] { 360d, 768d, 1280d })
                    {
                        hud.SizeToContent = SizeToContent.Height;
                        hud.Width = width;
                        Layout(hud);
                        foreach (var button in Descendants<Button>(hud))
                        {
                            var bounds = button.TransformToAncestor(hud).TransformBounds(new Rect(button.RenderSize));
                            Check(bounds.Right <= hud.ActualWidth && bounds.Bottom <= hud.ActualHeight,
                                $"浮条 {width} 宽时按钮溢出");
                        }
                    }
                    Descendants<Button>(hud).Single(b => b.Name == "LongShotFinish")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(hud.Cancelled, "完成按钮未结束截图");
                }
                finally { hud.Close(); }
            }
        }
        finally { ThemeService.ApplyTheme(FlashTrans.Core.AppTheme.Dark); }
    }

    static void SaveHud(Window window, string name)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--preview-dir");
        if (index < 0 || index + 1 >= args.Length) return;
        Directory.CreateDirectory(args[index + 1]);
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(args[index + 1], name));
        encoder.Save(file);
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

    static void RealScrollPixelsProbe(int footerHeight = 0)
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
        var surface = new Grid();
        surface.Children.Add(scroll);
        var footer = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(128, 220, 220, 220)),
        };
        surface.Children.Add(footer);
        var border = new Border { Background = Brushes.White, Child = surface };
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
            footer.Height = footerHeight / dpi.DpiScaleY;
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
            var expected = LongShotService.Crop(page, 0, page.Height - footerHeight)!;
            Check(result.Image!.Width == page.Width && result.Image.Height == expected.Height, "真实滚动拼接丢失了原始尺寸");
            Check(result.Image.Pixels.AsSpan().SequenceEqual(expected.Pixels), "真实抓屏或拼接发生缩放、模糊、底栏残留或像素错位");
            Console.WriteLine($"       实际底部浮层 {footerHeight} 像素，输出 {result.Image.Height} 行与原始正文逐像素一致");
        }
        finally { stop.Cancel(); window.Close(); }
    }

    static void RealManualScrollProbe()
    {
        var page = Page(1200);
        var picture = new Image
        {
            Source = page.ToBitmap(), Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
        var scroll = new ScrollViewer
        {
            Content = picture, CanContentScroll = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        };
        var window = new Window
        {
            Content = new Border { Background = Brushes.White, Child = scroll, Padding = new Thickness(12) },
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, UseLayoutRounding = true,
            ShowInTaskbar = false, Topmost = true, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 80, Top = 120, Width = 330, Height = 350,
        };
        var foreground = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var original);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<LongShotResult>? task = null;
        LongShotHud? hud = null;
        var height = 0;
        try
        {
            window.Show(); window.Activate(); Layout(window);
            var dpi = VisualTreeHelper.GetDpi(window);
            picture.Width = W / dpi.DpiScaleX;
            picture.Height = page.Height / dpi.DpiScaleY;
            scroll.Width = W / dpi.DpiScaleX + 24;
            scroll.Height = H / dpi.DpiScaleY;
            Layout(window);
            var origin = picture.PointToScreen(new Point(0, 0));
            var region = new RECT
            {
                Left = (int)Math.Round(origin.X), Top = (int)Math.Round(origin.Y),
                Right = (int)Math.Round(origin.X) + W, Bottom = (int)Math.Round(origin.Y) + H,
            };
            var targetHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Win32.SetCursorPos(region.Left + 8, region.Top + 8);
            SendMouse(0x0002); SendMouse(0x0004); Layout(window);
            Check(Win32.GetForegroundWindow() == targetHandle, "测试页面未取得焦点，不能验证浮条是否抢焦点");
            hud = new LongShotHud(region);
            hud.Show(); hud.Report(H, 1); Layout(hud);
            var mode = Descendants<Button>(hud).Single(b => b.Name == "LongShotMode");
            Click(mode);
            Check(hud.Automatic && !hud.Cancelled, "真实点击自动滚动没有切换模式");
            Click(mode);
            Check(!hud.Automatic && !hud.Cancelled, "真实点击手动滚动没有切回模式");
            Check(Win32.GetForegroundWindow() == targetHandle,
                $"浮条按钮抢走了被截窗口的焦点：目标={targetHandle}，浮条={new System.Windows.Interop.WindowInteropHelper(hud).Handle}，实际={Win32.GetForegroundWindow()}");
            Win32.SetCursorPos(region.Left + W / 2, region.Top + H / 2);
            task = Task.Run(() => LongShotService.RunAsync(region,
                onProgress: (value, frames) =>
                {
                    Volatile.Write(ref height, value);
                    window.Dispatcher.Invoke(() => hud.Report(value, frames));
                },
                cancelled: () => stop.IsCancellationRequested || hud.Cancelled, automatic: () => hud.Automatic));
            Wait(() => Volatile.Read(ref height) == H, "手动截图首帧没有就绪");
            SendMouse(Win32.MOUSEEVENTF_WHEEL, unchecked((uint)-Win32.WHEEL_DELTA));
            Wait(() => Volatile.Read(ref height) > H, "真实鼠标滚轮没有拼出新内容");

            var bar = Descendants<System.Windows.Controls.Primitives.ScrollBar>(scroll)
                .Single(b => b.Orientation == Orientation.Vertical);
            var thumb = Descendants<System.Windows.Controls.Primitives.Thumb>(bar).Single();
            var point = thumb.PointToScreen(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2));
            Win32.SetCursorPos((int)point.X, (int)point.Y);
            SendMouse(0x0002);
            Wait(() => thumb.IsMouseCaptured,
                $"滚动条未收到按下：焦点={Win32.GetForegroundWindow()}，目标={targetHandle}，命中={Mouse.DirectlyOver?.GetType().Name}");
            var beforeDrag = scroll.VerticalOffset;
            for (var i = 1; i <= 5; i++)
            {
                Win32.SetCursorPos((int)point.X, (int)point.Y + i * 8);
                var until = Environment.TickCount64 + 180;
                while (Environment.TickCount64 < until) { Layout(window); Thread.Sleep(5); }
            }
            SendMouse(0x0004);
            Wait(() => !thumb.IsMouseCaptured, "拖动结束后滚动条没有释放鼠标");
            Check(scroll.VerticalOffset > beforeDrag, "真实拖动没有滚动页面");
            var expectedHeight = H + (int)Math.Round(scroll.VerticalOffset * dpi.DpiScaleY);
            Wait(() => Volatile.Read(ref height) == expectedHeight, "拖动滚动条后没有拼到最新位置");
            Click(Descendants<Button>(hud).Single(b => b.Name == "LongShotFinish"));
            Win32.GetCursorPos(out var atFinish);
            Wait(() => task.IsCompleted, "完成手动截图后服务没有退出");
            var result = task.GetAwaiter().GetResult();
            Win32.GetCursorPos(out var afterFinish);
            Check(afterFinish.X == atFinish.X && afterFinish.Y == atFinish.Y, "手动结束后鼠标被移回起点");
            Check(result.Stopped == LongShotStop.Cancelled && result.Frames >= 3
                && result.Image!.Pixels.AsSpan().SequenceEqual(LongShotService.Crop(page, 0, expectedHeight)!.Pixels),
                "真实手动滚动拼接发生缺行、重复或像素变化");
            Console.WriteLine($"       手动滚轮 + 拖动滚动条：{result.Frames} 段，{expectedHeight} 行逐像素一致");
        }
        finally
        {
            SendMouse(0x0004);
            stop.Cancel();
            if (task is not null) Wait(() => task.IsCompleted, "手动截图测试清理超时");
            hud?.Close();
            window.Close();
            Win32.SetCursorPos(original.X, original.Y);
            if (foreground != IntPtr.Zero) Win32.SetForegroundWindow(foreground);
        }

        void Wait(Func<bool> done, string message)
        {
            var until = Environment.TickCount64 + 5000;
            while (!done() && Environment.TickCount64 < until) { Layout(window); Thread.Sleep(5); }
            Check(done(), message);
        }

        void Click(Button button)
        {
            var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            Win32.SetCursorPos((int)point.X, (int)point.Y);
            SendMouse(0x0002); SendMouse(0x0004);
            Layout(window);
        }

        static void SendMouse(uint flags, uint data = 0)
        {
            INPUT[] inputs = [new INPUT { type = Win32.INPUT_MOUSE,
                u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } } }];
            Check(Win32.SendInput(1, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>()) == 1,
                "无法向测试窗口发送鼠标输入");
        }
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
