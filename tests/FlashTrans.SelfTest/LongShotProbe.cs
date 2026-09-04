using FlashTrans.Interop;
using FlashTrans.Services;

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
}
