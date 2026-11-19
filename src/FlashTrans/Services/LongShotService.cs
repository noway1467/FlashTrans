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
    /// <summary>抓屏失败，可能仍有已接好的部分。</summary>
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
    const int ScrollKickDelayMs = 100;
    const int ScrollPollMs = 60;
    const int TransitionSamples = 36;
    const int StableSamples = 5;
    const int ForwardConfirmSamples = 5;
    const int BottomSamples = 12;
    const int MaxStationaryAttempts = 3;
    const int RecoveryAttempts = 2;
    const int MaxNotchesPerStep = 2;

    /// <summary>拼出来最高多少像素。再长就没人看了，也怕无限滚动的页面停不下来。</summary>
    public const int MaxHeight = 40000;
    /// <summary>最多滚多少次。</summary>
    public const int MaxFrames = 200;

    /// <summary>
    /// 在 region（屏幕物理像素）上滚着截。
    /// onProgress 每接上一帧调一次，参数是当前总高度和帧数。
    /// cancelled 返回 true 就停（用来接 Esc）。
    /// automatic 返回 false 时只观察用户滚动，不发送滚轮，也不因暂时没动而结束。
    /// </summary>
    public static async Task<LongShotResult> RunAsync(RECT region,
                                                      Action<int, int>? onProgress = null,
                                                      Func<bool>? cancelled = null,
                                                      Func<bool>? automatic = null,
                                                      Action<bool>? onAlignmentLost = null)
    {
        var restoreCursor = Win32.GetCursorPos(out var originalCursor);
        var session = ScreenCaptureSession.Open(
            region.Left, region.Top, region.Right - region.Left, region.Bottom - region.Top);
        try
        {
            var capture = session is null
                ? ScreenCapture.Grab
                : new Func<RECT, CapturedImage?>(_ => session.Grab());
            return await RunCoreAsync(region, onProgress, cancelled,
                                      capture, Wheel, DelayAsync, prepareWindow: true,
                                      automatic, onAlignmentLost);
        }
        finally
        {
            session?.Dispose();
            if (restoreCursor && (automatic?.Invoke() ?? true))
                Win32.SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    internal static async Task<LongShotResult> RunForTestAsync(
        RECT region,
        Func<RECT, CapturedImage?> capture,
        Action<int> scroll,
        Action<int, int>? onProgress = null,
        Func<bool>? cancelled = null,
        Func<int, Task>? delay = null,
        Func<bool>? automatic = null,
        Action<bool>? onAlignmentLost = null)
        => await RunCoreAsync(region, onProgress, cancelled, capture, scroll,
                              delay ?? DelayAsync, prepareWindow: false,
                              automatic, onAlignmentLost);

    static Task DelayAsync(int milliseconds) => Task.Delay(milliseconds);

    static async Task<LongShotResult> RunCoreAsync(
        RECT region,
        Action<int, int>? onProgress,
        Func<bool>? cancelled,
        Func<RECT, CapturedImage?> capture,
        Action<int> scroll,
        Func<int, Task> delay,
        bool prepareWindow,
        Func<bool>? automatic,
        Action<bool>? onAlignmentLost)
    {
        var w = region.Right - region.Left;
        var h = region.Bottom - region.Top;
        if (w <= 0 || h <= Band) return new LongShotResult(null, 0, LongShotStop.Failed);

        if (prepareWindow)
        {
            // 激活要在抓第一帧之前做完：标题栏的高亮会变，不然第一帧和后面的接缝处颜色不一样。
            var cx = region.Left + w / 2;
            var cy = region.Top + h / 2;
            FocusWindowAt(cx, cy);
            if (automatic?.Invoke() ?? true) MoveCursorToScrollSpot(region);
            await delay(120);
        }

        var first = await SettledGrabAsync(region, capture, delay, cancelled);
        if (first is null) return new LongShotResult(null, 0, LongShotStop.Failed);

        // 边接边攒。每段都是「新露出来的那几行」，最后一次性拼成一张。
        var parts = new List<CapturedImage> { first };
        var total = h;
        var prev = first;
        var band = PickBand(first);
        var fixedBottom = -1;
        var frames = 1;
        var automaticFrames = 1;
        // 先小步试。一格滚多远各家程序不一样，测出来之后下面会自己调。
        var notches = band > 150 ? 2 : 1;
        var stop = LongShotStop.Bottom;
        var stationaryAttempts = 0;
        ScrollFrame? manualCandidate = null;
        var manualStable = 0;
        var finishingSamples = -1;
        onProgress?.Invoke(total, frames);

        while (true)
        {
            var autoScroll = automatic?.Invoke() ?? true;
            if (cancelled?.Invoke() == true)
            {
                // 手动点完成时，给最后一段一次有界的稳定机会；刷新中的尾部不强行入图。
                if (autoScroll || finishingSamples == 0) { stop = LongShotStop.Cancelled; break; }
                if (finishingSamples < 0) finishingSamples = StableSamples + ForwardConfirmSamples + 2;
                finishingSamples--;
            }
            // 手动平滑滚动会产生许多很短的分片，沿用自动的次数限制会过早截断。
            if (total >= MaxHeight || (autoScroll && automaticFrames >= MaxFrames)) { stop = LongShotStop.Limit; break; }
            ScrollFrame? transition = null;
            if (!autoScroll)
            {
                stationaryAttempts = 0;
                await delay(ScrollPollMs);
                var frame = await Task.Run(() => capture(region));
                if (frame is null) { stop = LongShotStop.Failed; break; }
                var moved = await Task.Run(() =>
                {
                    var found = FindShift(prev, frame, band);
                    return found >= 0 || !Similar(prev, frame) ? found : 0;
                });
                onAlignmentLost?.Invoke(moved < 0);
                // 暂停、回滚或拖得太远时保留最后接好的帧，等用户回到仍有重叠的位置。
                if (moved <= 0) { manualStable = 0; continue; }
                if (manualCandidate is { } pending)
                {
                    var advance = moved - pending.Shift;
                    if (advance < 0)
                    {
                        manualCandidate = new ScrollFrame(frame, moved);
                        manualStable = 0;
                        continue;
                    }
                    var footer = Math.Max(Math.Max(0, fixedBottom), FindFixedBottom(prev, pending.Frame, pending.Shift));
                    var matches = await Task.Run(() => SimilarRows(pending.Frame, frame,
                        h - footer - pending.Shift, pending.Shift, advance));
                    manualStable = matches ? manualStable + 1 : 0;
                    // 连续滚动时，用后一帧中已上移的正文验证待拼区域；停住时等刷新稳定。
                    // 只对齐一条窄带就立刻接底部，会永久留下半帧、悬浮特效和透明横条。
                    if (matches && (advance > 0 || manualStable >= StableSamples + ForwardConfirmSamples))
                    {
                        transition = pending;
                        manualCandidate = advance > 0 ? new ScrollFrame(frame, advance) : null;
                        manualStable = 0;
                    }
                }
                if (transition is null)
                {
                    manualCandidate = new ScrollFrame(frame, moved);
                    continue;
                }
            }
            else
            {
                manualCandidate = null;
                manualStable = 0;
                onAlignmentLost?.Invoke(false);
                for (var attempt = 0; attempt <= RecoveryAttempts; attempt++)
                {
                    if (cancelled?.Invoke() == true || automatic?.Invoke() == false) break;
                    if (attempt == 0)
                    {
                        if (prepareWindow)
                        {
                            FocusWindowAt(region.Left + w / 2, region.Top + h / 2);
                            MoveCursorToScrollSpot(region);
                        }
                        scroll(-notches);
                    }

                    transition = await WaitForScrollAsync(
                        region, prev, band, capture, delay,
                        () => cancelled?.Invoke() == true || automatic?.Invoke() == false);
                    if (transition is not null) break;
                }
                if (automatic?.Invoke() == false) continue;
            }
            if (transition is null)
            {
                stop = cancelled?.Invoke() == true
                    ? LongShotStop.Cancelled
                    : LongShotStop.Diverged;
                break;
            }

            var next = transition.Value.Frame;
            var shift = transition.Value.Shift;
            if (shift == 0)
            {
                if (++stationaryAttempts < MaxStationaryAttempts)
                {
                    notches = 1;
                    continue;
                }
                break;                                  // 连续多次没动，才判定到底
            }
            stationaryAttempts = 0;

            // 横向滚动条、状态栏这类固定底栏不会跟正文一起滚。若仍从 next 最底下
            // 截 shift 行，它会在每个分片末尾重复一次，同时漏掉同等高度的正文。
            var nextFixedBottom = Math.Max(Math.Max(0, fixedBottom), FindFixedBottom(prev, next, shift));
            var trim = nextFixedBottom - Math.Max(0, fixedBottom);
            if (trim > 0)
            {
                // 浮动底栏可能在滚动后才出现；补裁上次接入的尾部，不能只检测首屏。
                total -= trim;
                while (trim > 0)
                {
                    var tail = parts[^1];
                    if (trim < tail.Height) { parts[^1] = Crop(tail, 0, tail.Height - trim)!; break; }
                    trim -= tail.Height;
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            fixedBottom = nextFixedBottom;

            // 只留新露出来的那部分。裁到剩余额度以内，别冲过高度上限。
            var take = Math.Min(shift, MaxHeight - total);
            var slice = Crop(next, next.Height - Math.Max(0, fixedBottom) - take, take);
            if (slice is null) break;

            parts.Add(slice);
            total += take;
            frames++;
            prev = next;
            band = PickBand(next);
            onProgress?.Invoke(total, frames);
            if (finishingSamples >= 0) { stop = LongShotStop.Cancelled; break; }

            // 下一次滚多少跟着实测走。上限是窄带能认出的最大位移，再多就对不上了，
            // 留三成余量。
            if (autoScroll)
            {
                automaticFrames++;
                var perNotch = Math.Max(1.0, (double)shift / notches);
                var safeDistance = Math.Max(1, band - Band);
                notches = Math.Clamp((int)(safeDistance * 0.7 / perNotch), 1, MaxNotchesPerStep);
            }
        }

        return new LongShotResult(Stack(parts, w), frames, stop);
    }

    // ------------------------------------------------------------- 抓帧

    /// <summary>
    /// 抓一帧，但要等画面稳住。平滑滚动会滚一小会儿，太早抓会拍到滚动中间的样子，
    /// 跟下一帧对不上。连续观察到画面足够接近才算稳。
    /// </summary>
    readonly record struct ScrollFrame(CapturedImage Frame, int Shift);

    static async Task<ScrollFrame?> WaitForScrollAsync(
        RECT region,
        CapturedImage previous,
        int bandTop,
        Func<RECT, CapturedImage?> capture,
        Func<int, Task> delay,
        Func<bool>? cancelled)
    {
        await delay(ScrollKickDelayMs);
        CapturedImage? last = null;
        var lastShift = int.MinValue;
        var sameShift = 0;
        var sameFrame = 0;
        var zeroFrames = 0;

        for (var i = 0; i < TransitionSamples; i++)
        {
            if (cancelled?.Invoke() == true) return null;
            var now = await Task.Run(() => capture(region));
            if (now is null) return null;

            var shift = await Task.Run(() =>
            {
                var found = FindShift(previous, now, bandTop);
                return found >= 0 || !Similar(previous, now) ? found : 0;
            });
            if (shift < 0)
            {
                lastShift = int.MinValue;
                sameShift = 0;
                sameFrame = 0;
                zeroFrames = 0;
            }
            else
            {
                if (shift == lastShift) sameShift++;
                else { lastShift = shift; sameShift = 1; }

                var similar = last is not null && await Task.Run(() => shift == 0
                    ? Similar(last, now)
                    : SimilarRows(last, now, now.Height - shift, shift));
                if (similar)
                    sameFrame++;
                else sameFrame = 1;

                if (shift == 0)
                {
                    zeroFrames++;
                    if (zeroFrames >= BottomSamples && sameFrame >= StableSamples)
                        return new ScrollFrame(now, 0);
                }
                else if (sameShift >= StableSamples + ForwardConfirmSamples
                         && sameFrame >= StableSamples + ForwardConfirmSamples)
                {
                    return new ScrollFrame(now, shift);
                }
                else
                {
                    zeroFrames = 0;
                }
            }

            last = now;
            await delay(ScrollPollMs);
        }

        return null;
    }

    static async Task<CapturedImage?> SettledGrabAsync(
        RECT region,
        Func<RECT, CapturedImage?> capture,
        Func<int, Task> delay,
        Func<bool>? cancelled = null)
    {
        if (cancelled?.Invoke() == true) return null;
        var prev = await Task.Run(() => capture(region));
        if (prev is null) return null;
        var stable = 0;

        for (var i = 0; i < 12; i++)
        {
            if (cancelled?.Invoke() == true) return null;
            await delay(40);
            var now = await Task.Run(() => capture(region));
            if (now is null) return prev;
            if (await Task.Run(() => Similar(prev, now))) stable++;
            else stable = 0;
            if (stable >= StableSamples) return now;
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

    static bool Similar(CapturedImage a, CapturedImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        var rows = 0;
        var different = 0;
        for (var y = 0; y < a.Height; y += 4)
        {
            rows++;
            if (!RowMatches(a, y, b, y, a.Width, 0.02) && ++different > Math.Max(1, rows / 20))
                return false;
        }
        return true;
    }

    static bool SimilarRows(CapturedImage a, CapturedImage b, int top, int height, int upwardShift = 0)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        top = Math.Clamp(top, 0, a.Height);
        if (upwardShift < 0 || top < upwardShift) return false;
        var bottom = Math.Clamp(top + height, top, a.Height);
        if (top >= bottom) return true;

        var rows = 0;
        var different = 0;
        for (var y = top; y < bottom; y += 4)
        {
            rows++;
            if (!RowMatches(a, y, b, y - upwardShift, a.Width, 0.02)
                && ++different > Math.Max(1, rows / 20))
                return false;
        }
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
            for (var d = 0; d <= maxShift; d++)
            {
                if (!BandMatches(prev, t, next, t - d, prev.Width)) continue;
                votes[d] = votes.TryGetValue(d, out var count) ? count + 1 : 1;
            }
        }

        if (votes.Count == 0) return -1;
        // 把零位移也参与比较：到底后的小动画不能让重复行被误当成又滚了一屏。
        var best = votes.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First();
        var eligible = anchors.Count(top => top >= best.Key);
        if (best.Value < Math.Max(1, (int)Math.Ceiling(eligible * 0.6))) return -1;
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

    /// <summary>用正文位移识别底部遮挡；透明底栏的像素会变，不能只比较同一位置是否不变。</summary>
    internal static int FindFixedBottom(CapturedImage prev, CapturedImage next, int shift)
    {
        if (shift <= 0 || prev.Width != next.Width || prev.Height != next.Height) return 0;

        var height = prev.Height;
        var top = Math.Max(shift, height - Math.Min(height / 3, 160));
        var mismatchRun = 0;
        var boundary = height;

        for (var y = height - 1; y >= top; y--)
        {
            if (RowMatches(prev, y, next, y - shift, prev.Width, RowTolerance)) mismatchRun = 0;
            else if (++mismatchRun >= 2) boundary = y;
        }

        if (boundary == height) return 0;
        // 必须有一段连续正文支持这个边界，不能把整屏刷新或错误位移当成底栏。
        var cleanRows = 0;
        for (var y = boundary - 1; y >= shift && cleanRows < Band; y--)
        {
            if (!RowMatches(prev, y, next, y - shift, prev.Width, RowTolerance)) break;
            cleanRows++;
        }
        return cleanRows >= Math.Min(Band, Math.Max(2, boundary - shift)) ? height - boundary : 0;
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
    static IntPtr FocusWindowAt(int x, int y)
    {
        var hwnd = Win32.WindowFromPoint(new POINT { X = x, Y = y });
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        // 命中的可能是个子控件，要的是它所属的顶层窗口
        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (root != IntPtr.Zero) hwnd = root;
        Win32.SetForegroundWindow(hwnd);
        return hwnd;
    }

    static void MoveCursorToScrollSpot(RECT region)
    {
        var width = Math.Max(1, region.Right - region.Left);
        var height = Math.Max(1, region.Bottom - region.Top);
        var x = Math.Clamp(region.Right - Math.Min(6, width), region.Left, region.Right - 1);
        var y = region.Top + height / 2;
        Win32.SetCursorPos(x, y);
    }

}
