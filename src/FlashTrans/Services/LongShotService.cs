using FlashTrans.Interop;

namespace FlashTrans.Services;

/// <summary>长截图的结果。Frames 是滚了多少屏，Stopped 说明为什么停下来的。</summary>
public sealed record LongShotResult(CapturedImage? Image, int Frames, LongShotStop Stopped);

public enum LongShotStop
{
    /// <summary>滚到底了，正常结束。</summary>
    Bottom,
    /// <summary>用户按了 Esc。已经拼到的部分照样给。</summary>
    Cancelled,
    /// <summary>撞到高度或帧数上限。</summary>
    Limit,
    /// <summary>画面变得跟上一帧接不上（翻页、动画、内容整体换了），只能就此为止。</summary>
    Diverged,
    /// <summary>连第一帧都没抓到。</summary>
    Failed,
}

/// <summary>
/// 长截图：在一块固定区域上反复「滚一点、抓一帧、跟上一帧对齐、把新露出来的接上去」。
///
/// 对齐不靠猜滚了多少像素——鼠标滚一格在不同程序里走的距离天差地别，平滑滚动还会
/// 滚到一半。办法是拿上一帧底部一条窄带去新帧里找它落到了哪儿，找到就知道真实位移。
/// </summary>
public static class LongShotService
{
    /// <summary>拿来对齐的窄带有多高（像素）。太窄容易在重复纹理上认错，太宽费时间。</summary>
    const int Band = 40;
    /// <summary>对齐时每隔几列取一个点。整行逐像素比没必要，抽样already够分辨。</summary>
    const int ColStep = 3;
    /// <summary>一行里允许多少比例的点对不上还算同一行。抗一点渲染抖动和亚像素文字。</summary>
    const double RowTolerance = 0.06;

    /// <summary>拼出来最高多少像素。再长就没人看了，也怕无限滚动的页面停不下来。</summary>
    public const int MaxHeight = 40000;
    /// <summary>最多滚多少次。</summary>
    public const int MaxFrames = 200;

    /// <summary>
    /// 在 region（屏幕物理像素）上滚着截。
    /// onProgress 每接上一帧调一次，参数是当前总高度和帧数。
    /// cancelled 返回 true 就停（用来接 Esc）。
    /// </summary>
    public static async Task<LongShotResult> RunAsync(RECT region,
                                                      Action<int, int>? onProgress = null,
                                                      Func<bool>? cancelled = null)
    {
        var w = region.Right - region.Left;
        var h = region.Bottom - region.Top;
        if (w <= 0 || h <= Band) return new LongShotResult(null, 0, LongShotStop.Failed);

        // 滚动落在鼠标底下那个窗口上，先把光标摆到区域中间并把那个窗口激活。
        // 激活要在抓第一帧之前做完：标题栏的高亮会变，不然第一帧和后面的接缝处颜色不一样。
        var cx = region.Left + w / 2;
        var cy = region.Top + h / 2;
        Win32.SetCursorPos(cx, cy);
        FocusWindowAt(cx, cy);
        await Task.Delay(120);

        var first = await SettledGrabAsync(region);
        if (first is null) return new LongShotResult(null, 0, LongShotStop.Failed);

        // 边接边攒。每段都是「新露出来的那几行」，最后一次性拼成一张。
        var parts = new List<CapturedImage> { first };
        var total = h;
        var prev = first;
        var band = PickBand(first);
        var frames = 1;
        // 先小步试。一格滚多远各家程序不一样，测出来之后下面会自己调。
        var notches = band > 150 ? 2 : 1;
        var retries = 0;
        var stop = LongShotStop.Bottom;

        while (true)
        {
            if (cancelled?.Invoke() == true) { stop = LongShotStop.Cancelled; break; }
            if (frames >= MaxFrames || total >= MaxHeight) { stop = LongShotStop.Limit; break; }

            Wheel(-notches);
            var next = await SettledGrabAsync(region);
            if (next is null) { stop = LongShotStop.Failed; break; }

            var shift = FindShift(prev, next, band);
            if (shift < 0)
            {
                // 对不上。要么这一下滚过了窄带看得见的范围，要么画面真的换了内容。
                // 先当成滚过了：退回去、把步子减半再来。连着几次都不行才认输——
                // 接不上就硬拼会在图里留一段没截到的空缺，那比少截一截糟糕得多。
                //
                // 退回去之后必须重新抓一帧当 prev。滚回来落点跟原来不一定分毫不差
                // （平滑滚动、行高取整都会差几像素），拿旧的 prev 去比就是在跟一个
                // 屏幕上从未出现过的画面算位移，量出来的 shift 偏一点，接缝处就会
                // 重复或者缺几行——就是长图上那些一段一段的错位。
                if (++retries <= 3)
                {
                    Wheel(notches);
                    await Task.Delay(80);
                    var back = await SettledGrabAsync(region);
                    if (back is not null)
                    {
                        prev = back;
                        band = PickBand(back);
                    }
                    // 步子已经是最小的还对不上，那就不是滚过头，是内容真变了
                    if (notches <= 1) { stop = LongShotStop.Diverged; break; }
                    notches = Math.Max(1, notches / 2);
                    continue;
                }
                stop = LongShotStop.Diverged;
                break;
            }
            retries = 0;
            if (shift == 0) break;                      // 滚不动了，到底

            // 只留新露出来的那部分。裁到剩余额度以内，别冲过高度上限。
            var take = Math.Min(shift, MaxHeight - total);
            var slice = Crop(next, next.Height - take, take);
            if (slice is null) break;

            parts.Add(slice);
            total += take;
            frames++;
            prev = next;
            band = PickBand(next);
            onProgress?.Invoke(total, frames);

            // 下一次滚多少跟着实测走。上限是窄带能认出的最大位移，再多就对不上了，
            // 留三成余量。
            var perNotch = Math.Max(1.0, (double)shift / notches);
            notches = Math.Clamp((int)(band * 0.7 / perNotch), 1, 15);
        }

        return new LongShotResult(Stack(parts, w), frames, stop);
    }

    // ------------------------------------------------------------- 抓帧

    /// <summary>
    /// 抓一帧，但要等画面稳住。平滑滚动会滚一小会儿，太早抓会拍到滚动中间的样子，
    /// 跟下一帧对不上。连着抓两张一样才算稳。
    /// </summary>
    static async Task<CapturedImage?> SettledGrabAsync(RECT region)
    {
        var prev = ScreenCapture.Grab(region);
        if (prev is null) return null;

        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(40);
            var now = ScreenCapture.Grab(region);
            if (now is null) return prev;
            if (Same(prev, now)) return now;
            prev = now;
        }
        // 一直在动（视频、动画）。就用最后这张，对齐那步会去判断能不能接上。
        return prev;
    }

    /// <summary>两帧是不是一样。抽样比，不用逐字节。</summary>
    static bool Same(CapturedImage a, CapturedImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (var y = 0; y < a.Height; y += 4)
            if (!RowMatches(a, y, b, y, a.Width, 0)) return false;
        return true;
    }

    // ------------------------------------------------------------- 对齐

    /// <summary>
    /// prev 里那条窄带在 next 里往上移了多少像素。
    /// 返回 0 表示没动（到底了），返回 -1 表示找不到——画面已经不是同一批内容了。
    ///
    /// 拼接对不对全看这个数。自测直接拿它和 PickBand / Stack 对着造好的图验，
    /// 不然只能靠真滚一遍屏幕，那测不出边界。
    /// </summary>
    internal static int FindShift(CapturedImage prev, CapturedImage next, int bandTop)
    {
        if (prev.Width != next.Width || prev.Height != next.Height) return -1;
        if (Same(prev, next)) return 0;

        var bottom = prev.Height - Band;
        if (bottom < Band) return -1;

        var start = Math.Clamp(bandTop, Band, bottom);
        var votes = new Dictionary<int, int>();
        var step = Math.Max(1, Band / 2);
        var anchors = new HashSet<int>();

        for (var t = start; t >= Band; t -= step)
            if (BandHasVariation(prev, t)) anchors.Add(t);
        if (BandHasVariation(prev, bottom)) anchors.Add(bottom);

        foreach (var t in anchors)
        {
            var maxShift = Math.Min(t, bottom);
            for (var d = 1; d <= maxShift; d++)
            {
                if (!BandMatches(prev, t, next, t - d, prev.Width)) continue;
                votes[d] = votes.TryGetValue(d, out var count) ? count + 1 : 1;
            }
        }

        if (votes.Count == 0) return -1;
        // 票数一样时取最小的位移。MaxBy 单用的话平票按字典枚举顺序决定，同一个画面
        // 两次跑可能给出不同的数；而且宁可少算也别多算——多算了会把没截到的内容当成
        // 「已经露出来过」跳掉，图上就缺一段；少算只是接缝处重复几行，看着不明显。
        var best = votes.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First();
        return best.Key;
    }

    /// <summary>
    /// 挑一条能用来对齐的窄带，返回它的顶边。
    ///
    /// 带子必须取在底部附近：内容往上走，只有下半部分在下一帧里还看得见。
    /// 但纯色的带子（页面留白、大片背景）在任何位置都能对上，那样第一个匹配到的
    /// 就是 d=0，程序会以为已经到底。所以从最底下往上找，挑一条竖向有变化的。
    /// </summary>
    internal static int PickBand(CapturedImage img)
    {
        var bottom = img.Height - Band;
        // 从最底下往上找第一条有内容的。位置越靠下，一次能认出的位移越大，
        // 所以宁可用靠下的；但一路找到顶也比拿一条纯色带去对齐强——
        // 只搜底下一小段的话，下方有整屏留白的页面会一条都找不到。
        for (var t = bottom; t >= Band; t -= Band / 2)
        {
            if (BandHasVariation(img, t)) return t;
        }
        // 整块都是平的。那也没什么可接的，返回底部让它按「到底了」收场。
        return bottom;
    }

    static bool BandHasVariation(CapturedImage img, int top)
    {
        var varied = 0;
        for (var i = 2; i < Band; i += 2)
            if (!RowMatches(img, top, img, top + i, img.Width, RowTolerance)) varied++;
        return varied >= 3;
    }

    static bool BandMatches(CapturedImage a, int ay, CapturedImage b, int by, int w)
    {
        // 先看首尾两行，不合就不用比中间了——绝大多数错位在这里就被否掉
        if (!RowMatches(a, ay, b, by, w, RowTolerance)) return false;
        if (!RowMatches(a, ay + Band - 1, b, by + Band - 1, w, RowTolerance)) return false;

        for (var i = 1; i < Band - 1; i += 2)
            if (!RowMatches(a, ay + i, b, by + i, w, RowTolerance)) return false;
        return true;
    }

    /// <summary>
    /// 两行像素够不够像。tolerance 是允许对不上的点占的比例，0 就是必须全等。
    /// </summary>
    static bool RowMatches(CapturedImage a, int ay, CapturedImage b, int by, int w, double tolerance)
    {
        var samples = (w + ColStep - 1) / ColStep;
        var budget = (int)(samples * tolerance);
        var pa = ay * a.Stride;
        var pb = by * b.Stride;
        var bad = 0;

        for (var x = 0; x < w; x += ColStep)
        {
            var ia = pa + x * 4;
            var ib = pb + x * 4;
            // 每个通道差 8 以内算同色。半透明合成、亚像素抗锯齿都会差出几个数。
            if (Math.Abs(a.Pixels[ia] - b.Pixels[ib]) <= 8
                && Math.Abs(a.Pixels[ia + 1] - b.Pixels[ib + 1]) <= 8
                && Math.Abs(a.Pixels[ia + 2] - b.Pixels[ib + 2]) <= 8) continue;

            if (++bad > budget) return false;
        }
        return true;
    }

    // ------------------------------------------------------------- 拼接

    /// <summary>从某一行起裁 h 行出来。</summary>
    internal static CapturedImage? Crop(CapturedImage src, int top, int h)
    {
        if (h <= 0 || top < 0 || top + h > src.Height) return null;
        var buf = new byte[src.Stride * h];
        Array.Copy(src.Pixels, top * src.Stride, buf, 0, buf.Length);
        return new CapturedImage(src.Width, h, buf);
    }

    /// <summary>把一叠等宽的图上下摞起来。</summary>
    internal static CapturedImage? Stack(List<CapturedImage> parts, int w)
    {
        if (parts.Count == 0) return null;
        if (parts.Count == 1) return parts[0];

        var h = parts.Sum(p => p.Height);
        var buf = new byte[w * 4 * h];
        var at = 0;
        foreach (var p in parts)
        {
            Array.Copy(p.Pixels, 0, buf, at, p.Pixels.Length);
            at += p.Pixels.Length;
        }
        return new CapturedImage(w, h, buf);
    }

    // ------------------------------------------------------------- 输入

    /// <summary>往下滚 notches 格（负数往下，跟 Windows 的方向一致）。</summary>
    static void Wheel(int notches)
    {
        var input = new INPUT[1];
        input[0].type = Win32.INPUT_MOUSE;
        input[0].u.mi = new MOUSEINPUT
        {
            mouseData = (uint)(notches * Win32.WHEEL_DELTA),
            dwFlags = Win32.MOUSEEVENTF_WHEEL,
        };
        Win32.SendInput(1, input, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// 把这个点上的窗口激活。滚轮消息默认送给前台窗口，不激活的话滚的是别人。
    /// </summary>
    static void FocusWindowAt(int x, int y)
    {
        var hwnd = Win32.WindowFromPoint(new POINT { X = x, Y = y });
        if (hwnd == IntPtr.Zero) return;
        // 命中的可能是个子控件，要的是它所属的顶层窗口
        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (root != IntPtr.Zero) hwnd = root;
        Win32.SetForegroundWindow(hwnd);
    }
}
