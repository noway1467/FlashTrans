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

public sealed record LongShotOptions(
    int MaxHeight = LongShotService.MaxHeight,
    int ScrollIntervalMs = LongShotService.DefaultScrollIntervalMs);

/// <summary>原像素尾部替换/追加，缩略预览自行缩放；Reset 用于首帧与视口收窄。</summary>
public sealed record LongShotPreviewPatch(
    int TrimTailRows, CapturedImage? AddedSlice, int TotalWidth, int TotalHeight, int Frames, bool Reset = false);

/// <summary>
/// 长截图：在一块固定区域上反复「滚一点、抓一帧、跟上一帧对齐、把新露出来的接上去」。
///
/// 用分散在正文中的局部块确定纵向位移，视频或动图没有匹配上的块不参与投票。
/// 拼接时在重叠区选择低差异接缝，保留单帧原像素，不混合文字或不同时刻的视频。
/// </summary>
public static class LongShotService
{
    /// <summary>检测正文纹理和固定边界的采样带高度，也用于限制自动滚动步长。</summary>
    const int Band = 40;
    /// <summary>像素比较的采样间隔，保留足够的细节而不逐像素扫描每个候选位移。</summary>
    const int ColStep = 3;
    /// <summary>一行里允许多少比例的点对不上还算同一行。抗一点渲染抖动和亚像素文字。</summary>
    const double RowTolerance = 0.06;
    const int ScrollPollMs = 60;
    const int TransitionSamples = 36;
    const int ShiftConfirmSamples = 10;
    const int BottomSamples = 12;
    const int MaxStationaryAttempts = 3;
    const int RecoveryAttempts = 2;
    const int MaxNotchesPerStep = 2;

    public const int MinMaxHeight = 4000;
    public const int MaxMaxHeight = 60000;
    public const int DefaultMaxHeight = MaxHeight;
    /// <summary>保留旧调用的默认上限；运行时可通过 LongShotOptions 调整。</summary>
    public const int MaxHeight = 40000;
    public const int MinScrollIntervalMs = 60;
    public const int MaxScrollIntervalMs = 600;
    public const int DefaultScrollIntervalMs = 100;
    public const string DefaultToggleHotkey = "F2";

    public static int ClampMaxHeight(int value) => Math.Clamp(value, MinMaxHeight, MaxMaxHeight);
    public static int ClampScrollIntervalMs(int value) => Math.Clamp(value, MinScrollIntervalMs, MaxScrollIntervalMs);
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
                                                      Action<bool>? onAlignmentLost = null,
                                                      Action<LongShotStop>? onAutomaticPaused = null,
                                                      Func<bool>? paused = null,
                                                      LongShotOptions? options = null,
                                                      Action<LongShotPreviewPatch>? onPreview = null)
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
                                      automatic, onAlignmentLost, onAutomaticPaused, paused, options, onPreview);
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
        Action<bool>? onAlignmentLost = null,
        Action<LongShotStop>? onAutomaticPaused = null,
        Func<bool>? paused = null,
        LongShotOptions? options = null,
        Action<LongShotPreviewPatch>? onPreview = null)
        => await RunCoreAsync(region, onProgress, cancelled, capture, scroll,
                              delay ?? DelayAsync, prepareWindow: false,
                              automatic, onAlignmentLost, onAutomaticPaused, paused, options, onPreview);

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
        Action<bool>? onAlignmentLost,
        Action<LongShotStop>? onAutomaticPaused,
        Func<bool>? paused,
        LongShotOptions? options,
        Action<LongShotPreviewPatch>? onPreview)
    {
        var maxHeight = ClampMaxHeight(options?.MaxHeight ?? MaxHeight);
        var scrollKickDelayMs = ClampScrollIntervalMs(options?.ScrollIntervalMs ?? DefaultScrollIntervalMs);
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

        while (paused?.Invoke() == true && cancelled?.Invoke() != true) await delay(ScrollPollMs);
        if (cancelled?.Invoke() == true) return new LongShotResult(null, 0, LongShotStop.Cancelled);
        var first = await Task.Run(() => capture(region));
        if (first is null) return new LongShotResult(null, 0, LongShotStop.Failed);
        if (first.Height > maxHeight) first = Crop(first, 0, maxHeight)!;

        // 边接边攒。每段都是「新露出来的那几行」，最后一次性拼成一张。
        var parts = new List<CapturedImage> { first };
        var total = first.Height;
        var prev = first;
        var band = PickBand(first);
        var fixedBottom = -1;
        var frames = 1;
        var automaticFrames = 1;
        // 先小步试。一格滚多远各家程序不一样，测出来之后下面会自己调。
        var notches = 1;
        var stop = LongShotStop.Bottom;
        var stationaryAttempts = 0;
        var viewportLocked = false;
        var unalignedFrames = 0;
        var lastSliceTop = 0;
        // 暂停可能打断已经发出滚轮的稳定采样；恢复先接完这一步，不能再多滚一次。
        var scrollPending = false;
        ReportPreview(new LongShotPreviewPatch(0, first, w, total, frames, Reset: true));
        onProgress?.Invoke(total, frames);

        while (true)
        {
            if (cancelled?.Invoke() == true)
            {
                stop = LongShotStop.Cancelled;
                break;
            }
            if (paused?.Invoke() == true)
            {
                await delay(ScrollPollMs);
                continue;
            }
            var autoScroll = automatic?.Invoke() ?? true;
            // 手动平滑滚动会产生许多很短的分片，沿用自动的次数限制会过早截断。
            if (total >= maxHeight || (autoScroll && automaticFrames >= MaxFrames)) { stop = LongShotStop.Limit; break; }
            ScrollFrame? transition = null;
            if (!autoScroll)
            {
                stationaryAttempts = 0;
                scrollPending = false;
                await delay(ScrollPollMs);
                if (paused?.Invoke() == true || cancelled?.Invoke() == true) continue;
                var frame = await Task.Run(() => capture(region));
                if (paused?.Invoke() == true || cancelled?.Invoke() == true) continue;
                if (frame is null) { stop = LongShotStop.Failed; break; }
                if (frames == 1 && !viewportLocked)
                {
                    var viewport = await Task.Run(() => FindScrollViewport(prev, frame));
                    if (paused?.Invoke() == true || cancelled?.Invoke() == true) continue;
                    if (viewport is { } area)
                    {
                        frame = Crop(frame, area);
                        UseViewport(area);
                    }
                }
                var moved = await Task.Run(() =>
                {
                    var found = FindShift(prev, frame);
                    return found >= 0 || !Similar(prev, frame) ? found : 0;
                });
                if (paused?.Invoke() == true || cancelled?.Invoke() == true) continue;
                unalignedFrames = moved < 0 ? unalignedFrames + 1 : 0;
                // 暂时缺少静态参照时先保留已有内容，局部动画本身不是失败条件。
                onAlignmentLost?.Invoke(unalignedFrames >= ShiftConfirmSamples);
                // 暂停、回滚或拖得太远时保留最后接好的帧，等用户回到仍有重叠的位置。
                if (moved == 0) { RefreshCurrent(frame); continue; }
                if (moved < 0) continue;
                transition = new ScrollFrame(frame, moved);
            }
            else
            {
                unalignedFrames = 0;
                onAlignmentLost?.Invoke(false);
                for (var attempt = 0; attempt <= RecoveryAttempts; attempt++)
                {
                    if (cancelled?.Invoke() == true || paused?.Invoke() == true || automatic?.Invoke() == false) break;
                    if (attempt == 0 && !scrollPending)
                    {
                        if (prepareWindow)
                        {
                            FocusWindowAt(region.Left + w / 2, region.Top + h / 2);
                            MoveCursorToScrollSpot(region);
                        }
                        scrollPending = true;
                        scroll(-notches);
                    }

                    transition = await WaitForScrollAsync(
                        region, prev, capture, delay,
                        () => cancelled?.Invoke() == true || paused?.Invoke() == true || automatic?.Invoke() == false,
                        scrollKickDelayMs,
                        detectViewport: frames == 1 && !viewportLocked);
                    if (transition is not null) break;
                }
                if (paused?.Invoke() == true) continue;
                if (automatic?.Invoke() == false) { scrollPending = false; continue; }
            }
            if (paused?.Invoke() == true) continue;
            if (transition is null)
            {
                scrollPending = false;
                if (cancelled?.Invoke() != true && automatic is not null && onAutomaticPaused is not null)
                {
                    Log.Warn($"长截图自动转手动：未对齐，已接 {frames} 段，{w}×{total} 像素。");
                    onAutomaticPaused(LongShotStop.Diverged);
                    continue;
                }
                stop = cancelled?.Invoke() == true
                    ? LongShotStop.Cancelled
                    : LongShotStop.Diverged;
                break;
            }

            var next = transition.Value.Frame;
            var shift = transition.Value.Shift;
            if (transition.Value.Viewport is { } selectedViewport)
            {
                next = Crop(next, selectedViewport);
                UseViewport(selectedViewport);
            }
            if (shift == 0)
            {
                scrollPending = false;
                RefreshCurrent(next);
                if (++stationaryAttempts < MaxStationaryAttempts)
                {
                    notches = 1;
                    continue;
                }
                if (automatic is not null && onAutomaticPaused is not null)
                {
                    // 屏幕不动只能说明本次滚轮没产生位移，不能证明嵌套页面已经到底。
                    Log.Warn($"长截图自动转手动：连续无位移，已接 {frames} 段，{w}×{total} 像素。");
                    onAutomaticPaused(LongShotStop.Bottom);
                    stationaryAttempts = 0;
                    continue;
                }
                break;                                  // 连续多次没动，才判定到底
            }
            stationaryAttempts = 0;

            // 横向滚动条、状态栏这类固定底栏不会跟正文一起滚。若仍从 next 最底下
            // 截 shift 行，它会在每个分片末尾重复一次，同时漏掉同等高度的正文。
            var nextFixedBottom = Math.Max(Math.Max(0, fixedBottom), FindFixedBottom(prev, next, shift));
            var trim = nextFixedBottom - Math.Max(0, fixedBottom);
            var contentBottom = next.Height - nextFixedBottom;
            var overlap = contentBottom - shift;
            var seam = await Task.Run(() => FindSeam(prev, next, shift, contentBottom));
            if (paused?.Invoke() == true || cancelled?.Invoke() == true) continue;
            var replace = overlap - seam;
            var trimmedTail = trim + Math.Max(0, replace);
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

            // 新内容之外可替换重叠的尾部，让接缝绕过视频/动图；整段来自同一帧，
            // 不把旧帧的视频上半部与新帧下半部机械地切在视口底边。
            if (replace > 0)
            {
                total -= replace;
                var remaining = replace;
                while (remaining > 0)
                {
                    var tail = parts[^1];
                    if (remaining < tail.Height) { parts[^1] = Crop(tail, 0, tail.Height - remaining)!; break; }
                    remaining -= tail.Height;
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            var take = Math.Min(replace + shift, maxHeight - total);
            var slice = Crop(next, seam, take);
            if (slice is null) break;

            parts.Add(slice);
            lastSliceTop = seam;
            total += slice.Height;
            frames++;
            prev = next;
            band = PickBand(next);
            scrollPending = false;
            ReportPreview(new LongShotPreviewPatch(trimmedTail, slice, w, total, frames));
            onProgress?.Invoke(total, frames);

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

        void UseViewport(RECT area)
        {
            // 内层正文滚动、外层固定时，先锁定正文视口。后面的对齐和抓取都用同一边界，
            // 不把固定侧栏、输入框或另一层滚动条重复拼进长图。
            var sourceCapture = capture;
            var sourceRegion = region;
            capture = _ => sourceCapture(sourceRegion) is { } image ? Crop(image, area) : null;
            region = new RECT
            {
                Left = sourceRegion.Left + area.Left, Top = sourceRegion.Top + area.Top,
                Right = sourceRegion.Left + area.Right, Bottom = sourceRegion.Top + area.Bottom,
            };
            first = Crop(first, area);
            viewportLocked = true;
            parts[0] = first;
            prev = first;
            w = first.Width;
            h = total = first.Height;
            band = PickBand(first);
            lastSliceTop = 0;
            ReportPreview(new LongShotPreviewPatch(0, first, w, total, frames, Reset: true));
            onProgress?.Invoke(total, frames);
        }

        void RefreshCurrent(CapturedImage frame)
        {
            // 位移为零时不增加长度，但让当前可见分片更新到最新完整画面。
            // 懒加载刚出现的空白可在加载后被替换，无需把“像素不变”当作拼接门槛。
            parts[^1] = Crop(frame, lastSliceTop, parts[^1].Height)!;
            prev = frame;
            if (frames == 1) first = frame;
            ReportPreview(new LongShotPreviewPatch(parts[^1].Height, parts[^1], w, total, frames));
        }

        void ReportPreview(LongShotPreviewPatch patch)
        {
            try { onPreview?.Invoke(patch); }
            catch (Exception ex) { Log.Warn("长截图实时预览回调失败：" + ex.Message); }
        }
    }

    // ------------------------------------------------------------- 抓帧

    /// <summary>
    /// 自动模式只等待滚动位移结束，不要求视频等局部内容停止变化。
    /// </summary>
    readonly record struct ScrollFrame(CapturedImage Frame, int Shift, RECT? Viewport = null);

    static async Task<ScrollFrame?> WaitForScrollAsync(
        RECT region,
        CapturedImage previous,
        Func<RECT, CapturedImage?> capture,
        Func<int, Task> delay,
        Func<bool>? cancelled,
        int scrollKickDelayMs,
        bool detectViewport = false)
    {
        // 可调等待仍每 60ms 检查暂停/取消，不让较慢的滚动间隔拖住快捷键响应。
        for (var remaining = scrollKickDelayMs; remaining > 0;)
        {
            if (cancelled?.Invoke() == true) return null;
            var wait = Math.Min(ScrollPollMs, remaining);
            await delay(wait);
            remaining -= wait;
        }
        var lastShift = int.MinValue;
        var sameShift = 0;
        var zeroFrames = 0;
        RECT? viewport = null;

        for (var i = 0; i < TransitionSamples; i++)
        {
            if (cancelled?.Invoke() == true) return null;
            var raw = await Task.Run(() => cancelled?.Invoke() == true ? null : capture(region));
            if (cancelled?.Invoke() == true) return null;
            if (raw is null) return null;
            if (detectViewport && viewport is null)
            {
                viewport = await Task.Run(() => FindScrollViewport(previous, raw));
                if (cancelled?.Invoke() == true) return null;
                if (viewport is { } area)
                {
                    previous = Crop(previous, area);
                    lastShift = int.MinValue;
                    sameShift = zeroFrames = 0;
                }
            }
            var now = viewport is { } bounds ? Crop(raw, bounds) : raw;

            var shift = await Task.Run(() =>
            {
                var found = FindShift(previous, now);
                return found >= 0 || !Similar(previous, now) ? found : 0;
            });
            if (cancelled?.Invoke() == true) return null;
            if (shift < 0)
            {
                lastShift = int.MinValue;
                sameShift = 0;
                zeroFrames = 0;
            }
            else
            {
                if (shift == lastShift) sameShift++;
                else { lastShift = shift; sameShift = 1; }

                if (shift == 0)
                {
                    zeroFrames++;
                    if (zeroFrames >= BottomSamples)
                        return new ScrollFrame(raw, 0, viewport);
                }
                else if (sameShift >= ShiftConfirmSamples)
                {
                    return new ScrollFrame(raw, shift, viewport);
                }
                else
                {
                    zeroFrames = 0;
                }
            }

            await delay(ScrollPollMs);
        }

        return null;
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

    // ------------------------------------------------------------- 对齐

    /// <summary>从首次滚动的画面差异定位内层视口，再用一致的纵向位移确认。</summary>
    internal static RECT? FindScrollViewport(CapturedImage previous, CapturedImage next)
    {
        if (previous.Width != next.Width || previous.Height != next.Height || Same(previous, next)) return null;
        var width = previous.Width;
        var height = previous.Height;
        var columns = new int[width];
        var top = height;
        var bottom = 0;
        for (var y = 0; y < height; y++)
        {
            var changed = 0;
            for (var x = 0; x < width; x++)
            {
                var i = y * previous.Stride + x * 4;
                if (Math.Abs(previous.Pixels[i] - next.Pixels[i]) <= 8
                    && Math.Abs(previous.Pixels[i + 1] - next.Pixels[i + 1]) <= 8
                    && Math.Abs(previous.Pixels[i + 2] - next.Pixels[i + 2]) <= 8) continue;
                columns[x]++;
                changed++;
            }
            if (changed < Math.Max(2, width / 100)) continue;
            top = Math.Min(top, y);
            bottom = y + 1;
        }
        var left = Array.FindIndex(columns, count => count >= Math.Max(2, height / 100));
        var right = Array.FindLastIndex(columns, count => count >= Math.Max(2, height / 100)) + 1;
        if (left < 0 || right - left < Band || bottom - top < Band * 2) return null;
        // 一块小动图或状态动画不能把整幅选区重定向到自己；小面板应单独框选。
        if ((long)(right - left) * (bottom - top) < (long)width * height / 8) return null;
        // 普通整块滚动继续走原路径，只有明显的固定边界才收窄视口。
        if (left == 0 && right == width && top < Band && bottom > height - Band) return null;

        // 探测时避开两侧的滚动条；确认位移后再逐列扩回正文和空白边距。
        var inset = Math.Min(16, (right - left) / 10);
        var probe = new RECT { Left = left + inset, Top = top, Right = right - inset, Bottom = bottom };
        var before = Crop(previous, probe);
        var after = Crop(next, probe);
        var shift = FindShift(before, after);
        if (shift <= 0) return null;

        left = probe.Left;
        right = probe.Right;
        while (left > 0 && ColumnMoves(left - 1, left)) left--;
        while (right < width && ColumnMoves(right, right - 1)) right++;
        var strip = new RECT { Left = left, Top = 0, Right = right, Bottom = height };
        before = Crop(previous, strip);
        after = Crop(next, strip);
        while (top > 0 && top - 1 + shift < height
            && RowMatches(before, top - 1 + shift, after, top - 1, before.Width, RowTolerance)) top--;
        while (bottom < height && bottom >= shift
            && RowMatches(before, bottom, after, bottom - shift, before.Width, RowTolerance)) bottom++;

        if (left == 0 && right == width && top == 0 && bottom == height) return null;
        var viewport = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        before = Crop(previous, viewport);
        after = Crop(next, viewport);
        if (FindShift(before, after) != shift) return null;
        var fixedEvidence = 0;
        for (var y = 4; y < height - 4; y += 4)
            for (var x = 0; x < width; x += ColStep)
            {
                // 留白在零位移和滚动位移下都可能相同，不能据此缩窄用户选区。
                var outside = (left >= Band && x < left) || (width - right >= Band && x >= right)
                    || (top >= Band && y < top) || (height - bottom >= Band && y >= bottom);
                if (!outside || !PixelMatches(previous, x, y, next, x, y)
                    || PixelMatches(previous, x, y, previous, x, y + 3)) continue;
                if (y >= shift && !PixelMatches(previous, x, y, next, x, y - shift)) fixedEvidence++;
            }
        return fixedEvidence >= 12 ? viewport : null;

        bool ColumnMoves(int x, int neighbor)
        {
            var samples = 0;
            var different = 0;
            var moving = 0;
            var edge = 0;
            for (var y = top + shift; y < bottom; y += ColStep)
            {
                samples++;
                var a = y * previous.Stride + x * 4;
                var b = (y - shift) * next.Stride + x * 4;
                var same = y * next.Stride + x * 4;
                var beside = y * previous.Stride + neighbor * 4;
                if (Math.Abs(previous.Pixels[a] - next.Pixels[b]) > 8
                    || Math.Abs(previous.Pixels[a + 1] - next.Pixels[b + 1]) > 8
                    || Math.Abs(previous.Pixels[a + 2] - next.Pixels[b + 2]) > 8) different++;
                if (Math.Abs(previous.Pixels[a] - next.Pixels[same]) > 8
                    || Math.Abs(previous.Pixels[a + 1] - next.Pixels[same + 1]) > 8
                    || Math.Abs(previous.Pixels[a + 2] - next.Pixels[same + 2]) > 8) moving++;
                if (Math.Abs(previous.Pixels[a] - previous.Pixels[beside]) > 8
                    || Math.Abs(previous.Pixels[a + 1] - previous.Pixels[beside + 1]) > 8
                    || Math.Abs(previous.Pixels[a + 2] - previous.Pixels[beside + 2]) > 8) edge++;
            }
            // 文字列中只有部分行带字；空白边距只需与相邻列的段落留白相接。
            // 滚动条轨道与正文空白之间通常有连续色差，不能把它扩进截图。
            return samples > 0 && different <= samples * RowTolerance
                && (moving > 0 || edge <= samples * 0.8);
        }
    }

    /// <summary>
    /// 多个有纹理的小块独立搜索纵向位移；共同支持的位移才可用于拼接。
    /// 返回 0 表示没动（到底了），返回 -1 表示找不到——画面已经不是同一批内容了。
    ///
    /// 拼接对不对全看这个数。自测直接拿它和 PickBand / Stack 对着造好的图验，
    /// 不然只能靠真滚一遍屏幕，那测不出边界。
    /// </summary>
    internal static int FindShift(CapturedImage prev, CapturedImage next)
    {
        if (prev.Width != next.Width || prev.Height != next.Height) return -1;
        if (Same(prev, next)) return 0;
        const int patchHeight = 16;
        var patchWidth = Math.Min(prev.Width, Math.Clamp(prev.Width / 16, 8, 24));
        var columns = Math.Min(16, Math.Max(1, prev.Width / patchWidth));
        var anchors = new List<(int X, int Y)>();
        for (var column = 0; column < columns; column++)
        {
            var x = columns == 1 ? 0 : column * (prev.Width - patchWidth) / (columns - 1);
            for (var y = prev.Height - patchHeight; y >= 0; y -= Math.Max(patchHeight, prev.Height / 24))
            {
                var variation = 0;
                for (var dy = 3; dy < patchHeight; dy += 3)
                    for (var dx = 0; dx < patchWidth; dx += 3)
                        if (!PixelMatches(prev, x + dx, y, prev, x + dx, y + dy)) variation++;
                if (variation >= 3) anchors.Add((x, y));
            }
        }
        var votes = new double[prev.Height];
        var uniqueVotes = new int[prev.Height];
        foreach (var (x, y) in anchors)
        {
            var matches = new List<int>();
            // 连反方向候选也用来判断是否重复，避免顶部重复文字因为不能再向上
            // 搜索，就被误认成“唯一的零位移”。最终只接受向下滚动产生的非负位移。
            for (var shift = y - (next.Height - patchHeight); shift <= y; shift++)
            {
                if (!PatchMatches(x, y, y - shift)) continue;
                matches.Add(shift);
            }
            foreach (var shift in matches)
            {
                if (shift < 0) continue;
                votes[shift] += 1.0 / matches.Count;
                if (matches.Count == 1 && y >= Band * 2) uniqueVotes[shift]++;
            }
        }
        var best = 0;
        for (var shift = 1; shift < votes.Length; shift++)
            if (uniqueVotes[shift] > uniqueVotes[best]
                || (uniqueVotes[shift] == uniqueVotes[best] && votes[shift] > votes[best])) best = shift;
        var eligible = anchors.Count(anchor => anchor.Y >= best);
        // 持续变化的块是离群值，不要求它们与正文一致；仍需多个独立块支持。
        if (uniqueVotes[best] < 3 && votes[best] < Math.Max(3, eligible * 0.1)) return -1;
        return best;

        bool PatchMatches(int x, int beforeY, int afterY)
        {
            // 首末行先否掉多数错误位移，减少大选区的逐像素工作量。
            if (!PixelMatches(prev, x, beforeY, next, x, afterY)
                || !PixelMatches(prev, x + patchWidth - 1, beforeY + patchHeight - 1,
                    next, x + patchWidth - 1, afterY + patchHeight - 1)) return false;
            var samples = 0;
            var different = 0;
            for (var dy = 0; dy < patchHeight; dy += 3)
                for (var dx = 0; dx < patchWidth; dx += 3)
                {
                    samples++;
                    if (!PixelMatches(prev, x + dx, beforeY + dy, next, x + dx, afterY + dy)) different++;
                }
            return different <= samples * RowTolerance;
        }
    }

    static bool PixelMatches(CapturedImage a, int ax, int ay, CapturedImage b, int bx, int by)
    {
        var pa = ay * a.Stride + ax * 4;
        var pb = by * b.Stride + bx * 4;
        return Math.Abs(a.Pixels[pa] - b.Pixels[pb]) <= 8
            && Math.Abs(a.Pixels[pa + 1] - b.Pixels[pb + 1]) <= 8
            && Math.Abs(a.Pixels[pa + 2] - b.Pixels[pb + 2]) <= 8;
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
            if (RowMatches(prev, y, next, y - shift, prev.Width, 0.9)) mismatchRun = 0;
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
        if (cleanRows < Math.Min(Band, Math.Max(2, boundary - shift))) return 0;
        var fixedRows = 0;
        for (var y = boundary; y < height; y++)
            if (RowMatches(prev, y, next, y, prev.Width, RowTolerance)) fixedRows++;
        if (fixedRows >= (height - boundary) * 0.9) return height - boundary;
        return IsTranslucentFooter(prev, next, shift, boundary) ? height - boundary : 0;
    }

    static bool IsTranslucentFooter(CapturedImage previous, CapturedImage next, int shift, int boundary)
    {
        // 半透明固定底栏会把正文作近似线性混色。只有这种证据才裁除，
        // 普通视频帧之间“不相同”不能被当成底栏，否则会把整个视频裁掉。
        var end = Math.Min(previous.Height, boundary + Math.Min(shift, Band));
        var informativeChannels = 0;
        for (var channel = 0; channel < 3; channel++)
        {
            double sumA = 0, sumB = 0, sumAA = 0, sumAB = 0;
            var count = 0;
            for (var y = boundary; y < end; y++)
                for (var x = 0; x < previous.Width; x += ColStep)
                {
                    var a = next.Pixels[(y - shift) * next.Stride + x * 4 + channel];
                    var b = previous.Pixels[y * previous.Stride + x * 4 + channel];
                    sumA += a; sumB += b; sumAA += a * a; sumAB += a * b; count++;
                }
            var variance = sumAA - sumA * sumA / count;
            if (variance < count * 16) continue;
            informativeChannels++;
            var slope = (sumAB - sumA * sumB / count) / variance;
            if (slope is < 0.1 or > 0.9) return false;
            var intercept = (sumB - slope * sumA) / count;
            var different = 0;
            for (var y = boundary; y < end; y++)
                for (var x = 0; x < previous.Width; x += ColStep)
                {
                    var a = next.Pixels[(y - shift) * next.Stride + x * 4 + channel];
                    var b = previous.Pixels[y * previous.Stride + x * 4 + channel];
                    if (Math.Abs(b - (slope * a + intercept)) > 8) different++;
                }
            if (different > count * RowTolerance) return false;
        }
        return informativeChannels > 0;
    }

    /// <summary>选择重叠区最靠后的低差异横向接缝；不同帧的视频只保留接缝一侧。</summary>
    internal static int FindSeam(CapturedImage previous, CapturedImage next, int shift, int contentBottom)
    {
        var overlap = contentBottom - shift;
        if (overlap <= 0) return 0;
        var best = overlap;
        var bestDifference = double.MaxValue;
        for (var seam = overlap; seam > 0; seam--)
        {
            var different = 0;
            var samples = 0;
            for (var y = Math.Max(0, seam - 3); y < Math.Min(contentBottom, seam + 3); y++)
            {
                if (y + shift >= previous.Height) break;
                for (var x = 0; x < previous.Width; x += ColStep)
                {
                    samples++;
                    if (!PixelMatches(previous, x, y + shift, next, x, y)) different++;
                }
            }
            var cost = samples == 0 ? 1 : (double)different / samples;
            if (cost < bestDifference) { bestDifference = cost; best = seam; }
            if (cost <= 0.02) return seam;
        }
        return best;
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

    internal static CapturedImage Crop(CapturedImage source, RECT area)
    {
        var width = area.Right - area.Left;
        var height = area.Bottom - area.Top;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
            Array.Copy(source.Pixels, (area.Top + y) * source.Stride + area.Left * 4, pixels, y * stride, stride);
        return new CapturedImage(width, height, pixels);
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
        // 嵌套页面最右侧往往是外层滚动条，滚正文中心才会落到内层容器。
        var x = region.Left + width / 2;
        var y = region.Top + height / 2;
        Win32.SetCursorPos(x, y);
    }

}
