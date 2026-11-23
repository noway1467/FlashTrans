using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlashTrans.Core;
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
        RunDynamic(step);
        step("长截图：固定底栏不会被重复拼进长图", FixedBottomProbe);
        step("长截图：普通底部留白不会被误裁", FixedBottomFalsePositiveProbe);
        step("长截图：能算出滚了多少像素", ShiftProbe);
        step("长截图：滚到底（画面没变）时算出 0", BottomProbe);
        step("长截图：画面整体换了内容时拒绝拼接", DivergeProbe);
        step("长截图：留白处不会被当成滚到底", FlatBandProbe);
        step("长截图：接起来的长图行数和内容都对", StackProbe);
        step("长截图：固定浮层不会被误判成滚到底", FixedOverlayProbe);
        step("长截图：较大位移仍用保留的局部重叠对齐", LargeShiftProbe);
        step("长截图：动态刷新和回弹期间不反向滚、不拼半帧", DynamicRefreshProbe);
        step("长截图：到底后的局部动画不把重复纹理当成继续滚动", BottomAnimationProbe);
        step("长截图：手动停留时刷新当前分片，加载后替换临时空白", ManualRefreshProbe);
        step("长截图：持续变化的尾部允许拼接，静态正文不重复", AnimatedTailProbe);
        step("长截图：透明底栏和中途出现的底栏不重复入图", TranslucentFooterProbe);
        step("长截图：手动与自动锁定内层滚动，排除固定外框和两层滚动条", () => NestedViewportProbe());
        step("长截图：嵌套文字区域保留空白边距和段落间隔", () => NestedViewportProbe(whitespace: true));
        step("长截图：局部动画不会把截图区域缩成动图", ViewportAnimationProbe);
        step("长截图：滚动事件延迟时不会提前判定到底", DelayedScrollProbe);
        step("长截图：手动停顿不结束，连续滚动逐像素拼接且不发送滚轮", ManualScrollProbe);
        step("长截图：手动回滚和过快滚动可恢复，不重复拼接或跳过内容", ManualRecoveryProbe);
        step("长截图：手动连续小幅滚动不提前触发自动次数上限", ManualSmallStepsProbe);
        step("长截图：自动等待期间切回手动，再开启自动不丢帧", ModeSwitchProbe);
        step("长截图：暂停期间零滚轮、零抓帧，手动和自动均逐像素续接", PauseResumeProbe);
        step("长截图：稳定采样中暂停立即退出，恢复不重复发送滚轮", PauseDuringSamplingProbe);
        step("长截图：首帧前暂停可取消，不抓屏不报失败", CancelBeforeFirstFrameProbe);
        step("长截图：自定义最大高度与滚动间隔实际生效", OptionsProbe);
        step("长截图：预览回调异常不影响主拼接", PreviewCallbackFaultProbe);
        step("长截图：自动中断保留现场，手动继续后再完成", AutomaticPauseProbe);
        step("长截图：短暂失配不提示回滚，持续失配提示可恢复", AlignmentFeedbackProbe);
        step("长截图：手动/自动浮条、完成按钮及深浅主题布局", HudProbe);
        step("长截图：开始/暂停键边沿、自动启动、手动接管和预览开关", HudStateProbe);
        RunFocus(step);
        step("长截图：实时预览左右定位与无空间安全隐藏", LivePreviewPlacementProbe);
        step("长截图：缩略增量精确裁尾、分片合并与视口重置", ThumbnailPatchProbe);
        step("长截图：100% 预览在不同 DPI 下保持物理像素一比一", PreviewDpiProbe);
        step("长截图：超长图分片虚拟化，缩放和滚到底后仍保留原像素", PreviewTilesProbe);
        step("长截图：默认适应宽度，记住窗口尺寸且最大化不覆盖普通尺寸", PreviewWindowSizeProbe);
        step("长截图：完成预览遵循默认视图设置，显式参数可覆盖", PreviewFitSettingProbe);
        step("长截图：无损 PNG 往返不缩图、不改细线像素", LosslessExportProbe);
        step("长截图：真实 WPF 滚动、GDI 抓屏和拼接保持原像素", () => RealScrollPixelsProbe());
        step("长截图：真实 WPF 半透明底部浮层不进入拼接结果", () => RealScrollPixelsProbe(footerHeight: 32));
        step("动态长截图：真实窗口动画持续播放时，GDI 抓屏仍完整拼接", () => RealScrollPixelsProbe(animated: true));
        step("长截图：真实手动滚轮和拖动滚动条，鼠标位置不被接管", () => RealManualScrollProbe());
        step("长截图：真实双层滚动条中手动滚轮和拖动内层仍能连续拼接", () => RealManualScrollProbe(nested: true));
    }

    public static void RunDynamic(Action<string, Action> step)
    {
        step("动态长截图：周围正文确定滚动位移，视频每帧变化不影响对齐", MovingVideoShiftProbe);
        step("动态长截图：自动和手动都能接完，视频保留一个完整时刻", () => MovingVideoStitchProbe());
        step("动态长截图：通栏播放的视频不会被误裁成固定底栏", () => MovingVideoStitchProbe(fullWidth: true));
        step("动态长截图：没有静态参照的全屏动画不伪造滚动", UnanchoredAnimationProbe);
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--longshot-browser-corpus");
        if (index >= 0 && index + 1 < args.Length)
            step("动态长截图：真实浏览器播放视频的抓帧重放，正文与视频完整性验证",
                () => BrowserVideoProbe(args[index + 1]));
    }

    public static void RunFocus(Action<string, Action> step)
    {
        step("长截图：真实热键不漏入页面，关闭释放注册，冲突降级可用", HudHotkeyProbe);
        step("长截图：实时预览深浅渲染、虚拟化、开关与不抢焦点", LivePreviewWindowProbe);
        step("长截图：宿主按设置打开预览或直接处理，None 动作仍弹预览", CompletionActionProbe);
    }

    static void BrowserVideoProbe(string directory)
    {
        CapturedImage Load(string name)
        {
            var bitmap = AnimEncoder.LoadFrame(Path.Combine(directory, name));
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return new CapturedImage(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
        }
        var reference = Load("browser-reference.png");
        var frames = Enumerable.Range(0, 11).Select(i => Load($"browser-{i * 200:D4}.png")).ToArray();
        Check(reference.Width == 900 && reference.Height == 2600 && frames.All(f => f.Width == 900 && f.Height == 600),
            "浏览器抓帧集尺寸不符合场景");
        Console.WriteLine("       相邻抓帧位移：" + string.Join(",", Enumerable.Range(1, frames.Length - 1)
            .Select(i => LongShotService.FindShift(frames[i - 1], frames[i]))));
        foreach (var automatic in new[] { false, true })
        {
            var index = 0;
            var ready = false;
            var samples = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var result = Task.Run(() => LongShotService.RunForTestAsync(new RECT { Right = 900, Bottom = 600 },
                _ =>
                {
                    if (ready && !automatic) index = Math.Min(++samples, frames.Length - 1);
                    return frames[index];
                }, _ => index = Math.Min(index + 1, frames.Length - 1),
                onProgress: (_, _) => ready = true, cancelled: () => samples > 15,
                delay: _ => Task.CompletedTask, automatic: () => automatic)).GetAwaiter().GetResult();
            Check(result.Image is not null && result.Image.Width == reference.Width && result.Image.Height == reference.Height,
                $"浏览器动态截图尺寸错误：{result.Image?.Width}×{result.Image?.Height}，{result.Stopped}");
            var image = result.Image!;
            for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                {
                    // Chromium 在非整数设备比例下会把 video 边缘合成到相邻一个像素。
                    if (x is >= 89 and < 811 && (y is >= 349 and < 571 || y is >= 1449 and < 1671)) continue;
                    var pixel = y * image.Stride + x * 4;
                    Check(image.Pixels.AsSpan(pixel, 4).SequenceEqual(reference.Pixels.AsSpan(pixel, 4)),
                        $"浏览器正文在 {x},{y} 出现重复、缺行或像素改变");
                }
            foreach (var top in new[] { 349, 1449 })
            {
                var intact = frames.Select((frame, i) => (Frame: frame, Offset: i * 200))
                    .Where(item => item.Offset <= top && item.Offset + 600 >= top + 222)
                    .Any(item => Enumerable.Range(0, 222).All(y => image.Pixels
                        .AsSpan((top + y) * image.Stride + 89 * 4, 722 * 4)
                        .SequenceEqual(item.Frame.Pixels.AsSpan((top - item.Offset + y) * image.Stride + 89 * 4, 722 * 4))));
                Check(intact, $"浏览器视频 {top} 被切成多个时刻，出现横向断层");
            }
            image.SavePng(Path.Combine(directory, automatic ? "browser-result-auto.png" : "browser-result-manual.png"));
            Console.WriteLine($"       {(automatic ? "自动" : "手动")}：{result.Frames} 段，900×2600，正文逐像素一致，两段视频均为完整单帧，{timer.ElapsedMilliseconds}ms");
        }
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

    static CapturedImage VideoFrame(CapturedImage page, int offset, int tick, bool fullWidth = false)
    {
        var frame = Frame(page, offset);
        const int videoTop = 180, videoBottom = 350;
        for (var y = Math.Max(0, videoTop - offset); y < Math.Min(H, videoBottom - offset); y++)
            for (var x = fullWidth ? 0 : 24; x < (fullWidth ? W : W - 24); x++)
            {
                var i = y * frame.Stride + x * 4;
                frame.Pixels[i] = (byte)(30 + tick % 5 * 40);
                frame.Pixels[i + 1] = (byte)(210 - tick % 5 * 35);
                frame.Pixels[i + 2] = (byte)(x % 2 == 0 ? 50 : 240);
            }
        return frame;
    }

    static void MovingVideoShiftProbe()
    {
        var page = Page(1200);
        foreach (var fullWidth in new[] { false, true })
        foreach (var shift in new[] { 0, 17, 60, 90, 150 })
        {
            var before = VideoFrame(page, 0, 0, fullWidth);
            var after = VideoFrame(page, shift, 2, fullWidth);
            Check(LongShotService.FindShift(before, after) == shift,
                $"视频变化时无法正确识别 {shift} 像素位移");
        }
    }

    static void MovingVideoStitchProbe(bool fullWidth = false)
    {
        var page = Page(1200);
        foreach (var automatic in new[] { false, true })
        {
            var ready = false;
            var offset = 0;
            var tick = 0;
            var samples = 0;
            var preview = new PreviewCollector();
            var result = Task.Run(() => LongShotService.RunForTestAsync(new RECT { Right = W, Bottom = H },
                _ =>
                {
                    if (!automatic && ready) offset = Math.Min(600, ++samples * 30);
                    return VideoFrame(page, offset, ++tick, fullWidth);
                }, _ => offset = Math.Min(600, offset + 60),
                onProgress: (_, _) => ready = true, cancelled: () => samples >= 40 || tick >= 300,
                delay: _ => Task.CompletedTask, automatic: () => automatic, onPreview: preview.Accept)).GetAwaiter().GetResult();
            preview.Verify(result);
            Check(preview.TrimmedRows > 0, "动态视频接缝没有通过增量预览同步尾部替换");
            Check(result.Image is not null && result.Image.Width == W && result.Image.Height == H + 600,
                $"{(automatic ? "自动" : "手动")}遇到播放中的视频就停住：{result.Stopped}，{result.Image?.Height} 行");
            var image = result.Image!;
            var videoColor = image.Pixels[180 * image.Stride + 24 * 4];
            for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                {
                    var i = y * image.Stride + x * 4;
                    if (y is >= 180 and < 350 && (fullWidth || x is >= 24 and < W - 24))
                        Check(image.Pixels[i] == videoColor, "同一个视频被不同时刻切成多段，产生横向折痕");
                    else Check(image.Pixels.AsSpan(i, 4).SequenceEqual(page.Pixels.AsSpan(i, 4)),
                        $"静态正文第 {y} 行缺失、重复或像素改变");
                }
        }
    }

    static void UnanchoredAnimationProbe()
    {
        var previous = Frame(Page(1200), 0);
        var next = new CapturedImage(W, H, previous.Pixels.Reverse().ToArray());
        Check(LongShotService.FindShift(previous, next) < 0,
            "全屏动画没有共同参照却猜出了滚动位移");
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
            var got = LongShotService.FindShift(prev, next);
            if (got != moved)
                throw new InvalidOperationException($"滚了 {moved} 行，算出来是 {got}");
        }
        Console.WriteLine($"       窄带上限 {band} 行，1 到 {band} 都算对了");
    }

    static void BottomProbe()
    {
        var page = Page(1200);
        var frame = Frame(page, 400);
        // 同一帧比自己：没动，应该是 0，调用方据此判定到底
        var got = LongShotService.FindShift(frame, frame);
        if (got != 0) throw new InvalidOperationException($"画面没变却算出滚了 {got} 行");
    }

    static void DivergeProbe()
    {
        var prev = Frame(Page(1200), 0);
        // 另一张毫不相干的页面：接不上就该说接不上，硬拼会在长图里留一段空缺
        var other = new CapturedImage(W, H, Page(1200).Pixels.Reverse().ToArray());
        var got = LongShotService.FindShift(prev, other);
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
        var got = LongShotService.FindShift(prev, next);
        if (got != 90)
            throw new InvalidOperationException($"固定浮层遮住底部时应算出 90，实际是 {got}");
    }

    static void LargeShiftProbe()
    {
        var page = Page(1200);
        var prev = Frame(page, 0);
        var next = Frame(page, 268);
        var got = LongShotService.FindShift(prev, next);
        if (got != 268)
            throw new InvalidOperationException($"滚了 268 行、仍有 32 行重叠时应算出 268，实际是 {got}");
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
        Check(LongShotService.FindShift(previous, next) == 0,
            "页面未滚动，底部小动画却让重复纹理被识别成滚动");
    }

    static void ManualRefreshProbe()
    {
        var page = Page(1200);
        var ready = false;
        var samples = 0;
        var captures = 0;
        var preview = new PreviewCollector();
        var result = Task.Run(() => LongShotService.RunForTestAsync(
            new RECT { Right = W, Bottom = H },
            _ =>
            {
                if (!ready) return Frame(page, 0);
                captures++;
                var offset = Math.Min(270, ((captures - 1) / 16 + 1) * 90);
                var next = Frame(page, offset);
                // 模拟浏览器下半部尚未完成合成；前几帧带半透明残影，之后才刷新干净。
                samples = (captures - 1) % 16;
                return captures <= 48 && samples < 6 ? WithTranslucentFooter(next, 55) : next;
            }, _ => throw new InvalidOperationException("手动模式发送了滚轮"),
            onProgress: (_, _) => ready = true,
            cancelled: () => captures >= 60,
            delay: _ => Task.CompletedTask, automatic: () => false, onPreview: preview.Accept)).GetAwaiter().GetResult();
        preview.Verify(result);
        Check(preview.Refreshes > 0, "懒加载刷新没有发送替换尾部的预览增量");
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
            Check(result.Frames == 2 && result.Image!.Height == H + 90,
                $"{(automatic ? "自动" : "手动")}因动态尾部无法拼接或重复增长");
            Check(result.Image!.Pixels.AsSpan(0, (H + 90 - 55) * page.Stride)
                .SequenceEqual(page.Pixels.AsSpan(0, (H + 90 - 55) * page.Stride)),
                "动态尾部影响了静态正文的拼接");
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
            var preview = new PreviewCollector();
            var result = Task.Run(() => LongShotService.RunForTestAsync(
                new RECT { Right = W, Bottom = H },
                _ =>
                {
                    var frame = Frame(page, Math.Min(270, scrolls * 90));
                    return scrolls >= appearsAt ? WithTranslucentFooter(frame, footer) : frame;
                }, _ => scrolls++, delay: _ => Task.CompletedTask, onPreview: preview.Accept)).GetAwaiter().GetResult();
            preview.Verify(result);
            Check(preview.TrimmedRows > 0, "裁除固定底栏没有同步预览");
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

    static void NestedViewportProbe(bool whitespace = false)
    {
        var page = Page(1200);
        if (whitespace)
            for (var y = 0; y < page.Height; y++)
                for (var x = 0; x < page.Width; x++)
                {
                    if (x >= 24 && x < W - 24 && y % 32 < 14) continue;
                    var i = y * page.Stride + x * 4;
                    page.Pixels[i] = page.Pixels[i + 1] = page.Pixels[i + 2] = 255;
                }
        const int left = 70, top = 70, width = W + 160, height = H + 200;
        CapturedImage Nested(int offset)
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    var color = RowColor((uint)(y + 5000));
                    pixels[i] = (byte)color;
                    pixels[i + 1] = (byte)(color >> 8);
                    pixels[i + 2] = (byte)(color >> 16);
                    pixels[i + 3] = 255;
                    if (x >= left + W && x < left + W + 16 && y >= top && y < top + H)
                    {
                        var thumb = y >= top + offset / 4 && y < top + offset / 4 + 50;
                        pixels[i] = pixels[i + 1] = pixels[i + 2] = thumb ? (byte)80 : (byte)210;
                    }
                }
            var inner = Frame(page, offset);
            for (var y = 0; y < H; y++)
                Array.Copy(inner.Pixels, y * inner.Stride, pixels, ((top + y) * width + left) * 4, inner.Stride);
            return new CapturedImage(width, height, pixels);
        }
        foreach (var automatic in new[] { false, true })
        {
            var ready = false;
            var samples = 0;
            var offset = 0;
            var preview = new PreviewCollector();
            var result = Task.Run(() => LongShotService.RunForTestAsync(
                new RECT { Right = width, Bottom = height },
                _ =>
                {
                    if (ready && !automatic) offset = Math.Min(240, ++samples / 4 * 30);
                    return Nested(offset);
                }, _ => offset = Math.Min(240, offset + 60),
                onProgress: (_, _) => ready = true, cancelled: () => !automatic && samples >= 70,
                delay: _ => Task.CompletedTask, automatic: () => automatic, onPreview: preview.Accept)).GetAwaiter().GetResult();
            preview.Verify(result);
            Check(preview.Resets == 2, "锁定内层视口没有重置实时预览宽度与首帧");
            Check(result.Image is not null && result.Image.Width == W && result.Image.Height == H + 240,
                $"嵌套区域边界错误：{result.Image?.Width}×{result.Image?.Height}，{result.Stopped}");
            Check(result.Image!.Pixels.AsSpan().SequenceEqual(LongShotService.Crop(page, 0, H + 240)!.Pixels),
                "嵌套滚动有缺行、固定边栏或滚动条残留");
        }
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

    static void ViewportAnimationProbe()
    {
        var page = Page(1200);
        var previous = Frame(page, 0);
        var next = Frame(page, 0);
        var animation = Frame(page, 30);
        for (var y = 120; y < 220; y++)
            Array.Copy(animation.Pixels, y * animation.Stride + 40 * 4,
                next.Pixels, y * next.Stride + 40 * 4, 60 * 4);
        Check(LongShotService.FindScrollViewport(previous, next) is null,
            "局部动图被当成了内层滚动区域，导致裁掉正文");
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
        Check(!lost.Any(value => value), "短暂回弹和失配不应立即要求用户回滚");
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

    static void AutomaticPauseProbe()
    {
        foreach (var diverged in new[] { false, true })
        {
            var page = Page(6000);
            var offset = 0;
            var automatic = true;
            var paused = false;
            var manualSamples = 0;
            var frames = 0;
            var result = Task.Run(() => LongShotService.RunForTestAsync(new RECT { Right = W, Bottom = H },
                _ =>
                {
                    if (paused && ++manualSamples == 1) offset += 90;
                    if (automatic && offset == 1080 && diverged) return Flat(H, 40);
                    return Frame(page, offset);
                },
                _ => { if (offset < 1080) offset += 90; },
                onProgress: (_, count) => frames = count,
                cancelled: () => frames >= (diverged ? 13 : 14) || manualSamples > 40,
                delay: _ => Task.CompletedTask, automatic: () => automatic,
                onAutomaticPaused: reason =>
                {
                    Check(reason == (diverged ? LongShotStop.Diverged : LongShotStop.Bottom), "自动暂停原因错误");
                    automatic = false;
                    paused = true;
                })).GetAwaiter().GetResult();
            Check(paused && result.Stopped == LongShotStop.Cancelled && result.Frames == (diverged ? 13 : 14),
                "自动未对齐或未继续滚动时直接结束，没有保留手动接管机会");
            Check(result.Image!.Pixels.AsSpan().SequenceEqual(LongShotService.Crop(page, 0, H + 1170)!.Pixels),
                "自动转手动丢失了已有长图或接错了新内容");
        }
    }

    static void AlignmentFeedbackProbe()
    {
        var page = Page(1200);
        var ready = false;
        var samples = 0;
        var feedback = new List<bool>();
        var result = Task.Run(() => LongShotService.RunForTestAsync(new RECT { Right = W, Bottom = H },
            _ => !ready ? Frame(page, 0) : ++samples <= 14 ? Flat(H, 30) : Frame(page, 90),
            _ => { }, onProgress: (_, _) => ready = true,
            cancelled: () => samples >= 35, delay: _ => Task.CompletedTask,
            automatic: () => false, onAlignmentLost: feedback.Add)).GetAwaiter().GetResult();
        Check(feedback.Take(9).All(value => !value) && feedback.Skip(9).Any(value => value) && !feedback.Last(),
            "短暂失配未等待、持续失配无提示或恢复后提示未清除");
        Check(result.Image!.Pixels.AsSpan().SequenceEqual(LongShotService.Crop(page, 0, H + 90)!.Pixels),
            "失配恢复后拼接内容错误");
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

    static void PauseResumeProbe()
    {
        foreach (var automatic in new[] { false, true })
        {
            var page = Page(1200);
            var offset = 0;
            var captures = 0;
            var wheels = 0;
            var reports = 0;
            var lostReports = 0;
            var paused = false;
            var pauseTicks = 0;
            var ready = false;
            var finished = false;
            (int Captures, int Wheels, int Reports, int Lost) beforePause = default;
            var result = Task.Run(() => LongShotService.RunForTestAsync(new RECT { Right = W, Bottom = H },
                _ =>
                {
                    Check(!paused, "暂停期间抓了新帧");
                    captures++;
                    if (!automatic && ready) offset = Math.Min(270, offset + 90);
                    return Frame(page, offset);
                }, _ =>
                {
                    Check(automatic && !paused, "手动或暂停期间发送了滚轮");
                    wheels++;
                    offset = Math.Min(270, offset + 90);
                }, onProgress: (_, frames) =>
                {
                    ready = true;
                    reports++;
                    if (frames == 2)
                    {
                        paused = true;
                        beforePause = (captures, wheels, reports, lostReports);
                    }
                    if (frames == 4) finished = true;
                }, cancelled: () => finished,
                delay: milliseconds =>
                {
                    if (paused)
                    {
                        Check(milliseconds <= 60, "暂停轮询没有使用短周期");
                        Check(beforePause == (captures, wheels, reports, lostReports), "暂停累计了帧、滚轮或失配反馈");
                        if (++pauseTicks == 4) paused = false;
                    }
                    return Task.CompletedTask;
                }, automatic: () => automatic, paused: () => paused,
                onAlignmentLost: _ => lostReports++,
                onAutomaticPaused: _ => throw new InvalidOperationException("暂停被误报为自动滚动失败"))).GetAwaiter().GetResult();
            Check(pauseTicks == 4 && result.Stopped == LongShotStop.Cancelled && result.Frames == 4,
                "暂停后没有继续完成拼接");
            Check(result.Image!.Pixels.AsSpan().SequenceEqual(LongShotService.Crop(page, 0, H + 270)!.Pixels),
                "暂停恢复后有缺行、重复或像素变化");
        }
    }

    static void PauseDuringSamplingProbe()
    {
        var page = Page(1200);
        var wheels = 0;
        var captures = 0;
        var paused = false;
        var pauseTicks = 0;
        var interrupted = false;
        var finished = false;
        var result = Task.Run(() => LongShotService.RunForTestAsync(new RECT { Right = W, Bottom = H },
            _ => { Check(!paused, "稳定采样暂停后仍抓帧"); captures++; return Frame(page, wheels * 90); },
            _ => { Check(!paused, "暂停后仍发送滚轮"); wheels++; },
            onProgress: (_, frames) => { if (frames == 3) finished = true; }, cancelled: () => finished,
            delay: milliseconds =>
            {
                if (paused)
                {
                    Check(captures == 1 && wheels == 1 && milliseconds <= 60, "暂停没有中断滚动等待");
                    if (++pauseTicks == 4) paused = false;
                }
                else if (wheels == 1 && !interrupted) { interrupted = true; paused = true; }
                return Task.CompletedTask;
            }, automatic: () => true, paused: () => paused,
            options: new LongShotOptions(ScrollIntervalMs: 600),
            onAutomaticPaused: _ => throw new InvalidOperationException("采样暂停被误报失配"))).GetAwaiter().GetResult();
        Check(interrupted && pauseTicks == 4 && wheels == 2 && result.Frames == 3 && result.Stopped == LongShotStop.Cancelled,
            "恢复后重复滚了一步或提前结束");
        Check(result.Image!.Pixels.AsSpan().SequenceEqual(LongShotService.Crop(page, 0, H + 180)!.Pixels),
            "稳定采样暂停恢复丢失了拼接基准");
    }

    static void CancelBeforeFirstFrameProbe()
    {
        var ticks = 0;
        var result = Task.Run(() => LongShotService.RunForTestAsync(new RECT { Right = W, Bottom = H },
            _ => throw new InvalidOperationException("首帧前暂停仍抓屏"),
            _ => throw new InvalidOperationException("首帧前暂停仍滚动"),
            cancelled: () => ticks >= 4, paused: () => true,
            delay: _ => { ticks++; return Task.CompletedTask; })).GetAwaiter().GetResult();
        Check(result.Image is null && result.Frames == 0 && result.Stopped == LongShotStop.Cancelled,
            "首帧前暂停取消被报为抓屏失败");
    }

    static void OptionsProbe()
    {
        var page = Page(6000);
        var offset = 0;
        var preview = new PreviewCollector();
        var result = Task.Run(() => LongShotService.RunForTestAsync(new RECT { Right = W, Bottom = H },
            _ => Frame(page, offset), _ => offset += 200, delay: _ => Task.CompletedTask,
            options: new LongShotOptions(MaxHeight: 4000), onPreview: preview.Accept)).GetAwaiter().GetResult();
        preview.Verify(result);
        Check(result.Stopped == LongShotStop.Limit && result.Image?.Height == 4000
            && result.Image.Pixels.AsSpan().SequenceEqual(LongShotService.Crop(page, 0, 4000)!.Pixels),
            "自定义高度上限未触发 Limit 或末片裁剪不正确");

        var firstOnly = Task.Run(() => LongShotService.RunForTestAsync(new RECT { Right = W, Bottom = 5000 },
            _ => LongShotService.Crop(page, 0, 5000), _ => throw new InvalidOperationException("首屏超限仍发送滚轮"),
            delay: _ => Task.CompletedTask, options: new LongShotOptions(MaxHeight: 3999))).GetAwaiter().GetResult();
        Check(firstOnly.Stopped == LongShotStop.Limit && firstOnly.Frames == 1 && firstOnly.Image?.Height == 4000,
            "首屏没有遵循夹取后的高度上限");

        foreach (var requested in new[] { 1, 100, 380, 1000 })
        {
            var clock = 0;
            var kickedAt = 0;
            var firstSampleDelay = -1;
            var scrolled = false;
            var finished = false;
            var timed = Task.Run(() => LongShotService.RunForTestAsync(new RECT { Right = W, Bottom = H },
                _ =>
                {
                    if (scrolled && firstSampleDelay < 0) firstSampleDelay = clock - kickedAt;
                    return Frame(page, scrolled ? 90 : 0);
                }, _ => { scrolled = true; kickedAt = clock; },
                onProgress: (_, frames) => finished = frames == 2, cancelled: () => finished,
                delay: milliseconds => { clock += milliseconds; return Task.CompletedTask; },
                options: new LongShotOptions(ScrollIntervalMs: requested))).GetAwaiter().GetResult();
            Check(timed.Frames == 2 && firstSampleDelay == LongShotService.ClampScrollIntervalMs(requested),
                $"自定义间隔 {requested}ms 未传入实际采样等待：{firstSampleDelay}ms");
        }
    }

    static void PreviewCallbackFaultProbe()
    {
        var page = Page(1200);
        var offset = 0;
        var callbacks = 0;
        var result = Task.Run(() => LongShotService.RunForTestAsync(new RECT { Right = W, Bottom = H },
            _ => Frame(page, offset), _ => offset = Math.Min(270, offset + 90), delay: _ => Task.CompletedTask,
            onPreview: _ => { callbacks++; throw new InvalidOperationException("预览回调故障注入"); })).GetAwaiter().GetResult();
        Check(callbacks > 1 && result.Stopped == LongShotStop.Bottom && result.Frames == 4
            && result.Image!.Pixels.AsSpan().SequenceEqual(LongShotService.Crop(page, 0, H + 270)!.Pixels),
            "预览回调异常中断了原图拼接");
    }

    static void HudStateProbe()
    {
        var notices = new List<bool>();
        var region = new RECT { Left = 80, Top = 80, Right = 400, Bottom = 420 };
        var hud = new LongShotHud(region, "F2", livePreviewOn: true, onLivePreviewToggled: notices.Add);
        try
        {
            hud.WatchEsc(true);
            hud.WatchToggleChord(true);
            hud.ToggleScroll();
            Check(!hud.Cancelled && !hud.Automatic && !hud.Paused, "首帧前按住键误开始或误完成");
            hud.Report(H, 1);
            hud.WatchToggleChord(true);
            Check(!hud.Automatic, "初始按住的快捷键未等待松开");
            hud.WatchToggleChord(false); hud.WatchToggleChord(true); hud.WatchToggleChord(true);
            Check(hud.Automatic && !hud.Paused, "首次按下未开始或按住键反复切换");
            hud.WatchToggleChord(false); hud.WatchToggleChord(true); hud.WatchToggleChord(true);
            Check(!hud.Automatic && hud.Paused, "第二次按下没有暂停");
            hud.Report(H + 90, 2);
            Check(hud.Paused, "在途进度报告解除了用户暂停");
            hud.WatchToggleChord(false); hud.WatchToggleChord(true);
            Check(hud.Automatic && !hud.Paused, "暂停后没有继续自动滚动");
            hud.UseManualScroll();
            Check(!hud.Automatic && !hud.Paused, "切回手动仍冻结抓帧");
            hud.ToggleScroll();
            Check(hud.Automatic, "手动接管后不能再开始自动滚动");
            hud.ToggleLivePreview(); hud.ToggleLivePreview();
            Check(hud.LivePreviewVisible && notices.SequenceEqual(new[] { false, true }), "实时预览开关没有通知宿主");
            hud.ReportLivePreview(false, "选区两侧空间不足，实时预览已隐藏");
            Check(!hud.LivePreviewVisible && Descendants<TextBlock>((Border)hud.Content).Any(t => t.Text.Contains("空间不足")),
                "预览放不下时没有安全回告和提示");
            Check(Descendants<Button>((Border)hud.Content).Single(b => b.Name == "LongShotMode").Content.ToString()!.Contains("F2"),
                "开始/暂停按钮没有显示当前快捷键");
            hud.WatchEsc(false); hud.WatchEsc(true);
            Check(hud.Cancelled, "Esc 松开再按下没有完成截图");
        }
        finally { hud.Close(); }

        var auto = new LongShotHud(region, "", autoStart: true);
        try
        {
            auto.ToggleScroll();
            Check(!auto.Automatic, "默认自动在首帧前发送滚轮");
            auto.Report(H, 1);
            Check(auto.Automatic && !auto.Paused, "首帧就绪没有按设置自动开始");
            auto.ToggleScroll(); auto.Report(H + 90, 2);
            Check(auto.Paused && !auto.Automatic, "后续进度再次触发了默认自动开始");
            Check(!Descendants<Button>((Border)auto.Content).Single(b => b.Name == "LongShotMode").Content.ToString()!.Contains('('),
                "关闭快捷键时按钮仍有空括号");
            Check(Descendants<Button>((Border)auto.Content).Single(b => b.Name == "LongShotPreview").Visibility == Visibility.Collapsed,
                "没有预览回调仍显示预览按钮");
        }
        finally { auto.Close(); }
    }

    static void HudHotkeyProbe()
    {
        var spec = AvailableToggleHotkey();
        var foreground = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var cursor);
        var target = new Window { Content = new TextBox(), Width = 300, Height = 140, ShowInTaskbar = false, Topmost = true };
        LongShotHud? hud = null;
        RECT region = default;
        var mouseToggleClicks = 0;
        var leaked = 0;
        var hotkeyMessages = 0;
        var released = 0;
        var hudHandle = IntPtr.Zero;
        ThreadMessageEventHandler trace = (ref System.Windows.Interop.MSG message, ref bool handled) =>
        {
            if (message.hwnd == hudHandle && message.message == Win32.WM_HOTKEY) hotkeyMessages++;
            if (message.message is Win32.WM_KEYUP or Win32.WM_SYSKEYUP && message.wParam.ToInt32() == spec.VirtualKey) released++;
        };
        ComponentDispatcher.ThreadFilterMessage += trace;
        target.PreviewKeyDown += (_, e) => { if (e.Key == spec.Key || e.SystemKey == spec.Key) leaked++; };
        try
        {
            target.Show(); Layout(target); FocusTarget(target); ((TextBox)target.Content).Focus();
            var targetHandle = new WindowInteropHelper(target).Handle;
            Check(Win32.GetWindowRect(targetHandle, out region), "无法取得热键测试窗口的真实区域");
            hud = new LongShotHud(region, spec.ToString());
            Descendants<Button>((Border)hud.Content).Single(b => b.Name == "LongShotMode").Click += (_, _) => mouseToggleClicks++;
            hud.Show(); hud.Report(H, 1); Layout(hud);
            hudHandle = new WindowInteropHelper(hud).Handle;
            Check((bool)typeof(LongShotHud).GetField("_hotkeyOk", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(hud)!,
                "空闲测试键没有注册在 HUD 上，不能当作原生热键验证");
            Check(!hud.Automatic && !hud.Paused && mouseToggleClicks == 0, "准备焦点的点击误落到浮条上，尚未按热键就切换了状态");
            InjectHotkey(spec, target, repeats: 4);
            WaitFor(() => hud.Automatic, target, "真实长截图快捷键没有开始自动滚动");
            Check(!hud.Paused && leaked == 0 && Win32.GetForegroundWindow() == targetHandle,
                $"注册热键或焦点不正确：键={spec}，暂停={hud.Paused}，漏键={leaked}，目标={targetHandle}，前台={Win32.GetForegroundWindow()}");
            InjectHotkey(spec, target, repeats: 3);
            try { WaitFor(() => hud.Paused, target, "真实快捷键没有暂停滚动"); }
            catch
            {
                Console.WriteLine($"       热键诊断：{spec}，WM_HOTKEY={hotkeyMessages}，松开消息={released}，漏键={leaked}，自动={hud.Automatic}，暂停={hud.Paused}，完成={hud.Cancelled}");
                throw;
            }
            Check(!hud.Automatic && leaked == 0, "按住热键来回翻转或泄漏进页面");
            InjectHotkey(new HotkeySpec(spec.Modifiers | ModifierKeys.Shift, spec.Key), target); Layout(target);
            Check(hud.Paused, "额外修饰键仍触发长截图切换");
            InjectHotkey(spec, target);
            WaitFor(() => hud.Automatic && !hud.Paused, target, "真实快捷键没有继续滚动");
            Check(hotkeyMessages == 3, $"按住按键触发了多余热键消息：{hotkeyMessages}");
            Check(mouseToggleClicks == 0, "焦点准备的鼠标输入混入了热键状态验证");
            Console.WriteLine($"       {spec}：开始、暂停、继续各 1 条 WM_HOTKEY，按住不连发，前两次按键未漏入页面");
        }
        catch
        {
            Console.WriteLine($"       热键现场：{spec}，WM_HOTKEY={hotkeyMessages}，鼠标切换={mouseToggleClicks}，漏键={leaked}，自动={hud?.Automatic}，暂停={hud?.Paused}，完成={hud?.Cancelled}，前台={Win32.GetForegroundWindow()}");
            throw;
        }
        finally
        {
            ComponentDispatcher.ThreadFilterMessage -= trace;
            hud?.Close(); target.Close(); Win32.SetCursorPos(cursor.X, cursor.Y);
            if (foreground != IntPtr.Zero) Win32.SetForegroundWindow(foreground);
        }

        using var blocker = new MessageWindow("LongShot.Toggle.Blocker");
        Check(Win32.RegisterHotKey(blocker.Handle, 0x7161, spec.Win32Modifiers, spec.VirtualKey), "HUD 关闭后没有释放快捷键");
        var fallback = new LongShotHud(region, spec.ToString());
        try
        {
            fallback.Show(); fallback.Report(H, 1); Layout(fallback);
            fallback.WatchToggleChord(false); fallback.WatchToggleChord(true); fallback.WatchToggleChord(true);
            Check(fallback.Automatic && !fallback.Paused, "注册失败后边沿轮询不可用");
            fallback.WatchToggleChord(false); fallback.WatchToggleChord(true); fallback.WatchToggleChord(true);
            Check(!fallback.Automatic && fallback.Paused, "注册失败后暂停轮询重复翻转");
        }
        finally { fallback.Close(); Win32.UnregisterHotKey(blocker.Handle, 0x7161); }
    }

    static void LivePreviewPlacementProbe()
    {
        var monitor = new Rect(0, 0, 1280, 720);
        var right = Verify(new Rect(100, 80, 680, 500), monitor);
        Check(right.Left >= 790, "有空间时未优先右置预览");
        var left = Verify(new Rect(420, 80, 800, 500), monitor);
        Check(left.Right <= 410, "右侧不足时未改为左置");
        var narrow = Verify(new Rect(40, 80, 1000, 500), monitor);
        Check(narrow.Width is >= 176 and < 240, "侧边有限空间没有缩窄预览");
        var exactRight = Verify(new Rect(40, 80, 1054, 500), monitor);
        var exactLeft = Verify(new Rect(186, 80, 1050, 500), monitor);
        Check(exactRight.Width == 176 && exactLeft.Width == 176, "恰好最小宽度的侧边空间没有使用");
        Verify(new Rect(-1100, -30, 600, 1000), new Rect(-1280, 0, 1280, 720));
        Verify(new Rect(20, 20, 120, 180), new Rect(0, 0, 360, 300));
        foreach (var selection in new[] { monitor, new Rect(10, 20, 1260, 640), new Rect(150, 20, 1000, 650) })
            Check(LongShotLivePreviewWindow.TryPlaceOutside(selection, monitor) is null, "左右不足仍将预览压入选区");

        static Rect Verify(Rect selection, Rect monitor)
        {
            var placed = LongShotLivePreviewWindow.TryPlaceOutside(selection, monitor)
                ?? throw new InvalidOperationException("有足够侧边空间却没有放置预览");
            Check(!placed.IntersectsWith(selection) && monitor.Contains(placed) && placed.Width >= 176,
                "实时预览与选区相交或越过显示器边界");
            return placed;
        }
    }

    static void ThumbnailPatchProbe()
    {
        var page = DetailedPage(440, 5000);
        var replacement = DetailedPage(440, 1001);
        var buffer = new LongShotLivePreviewWindow.ThumbnailBuffer();
        var stripes = new List<BitmapSource>();
        Apply(new LongShotPreviewPatch(0, LongShotService.Crop(page, 0, 1401), 440, 1401, 1, Reset: true));
        var firstStripe = stripes[0];
        Apply(new LongShotPreviewPatch(0, LongShotService.Crop(page, 1401, 1300), 440, 2701, 2));
        Check(ReferenceEquals(firstStripe, stripes[0]), "追加重建了未变化的预览条带");
        Apply(new LongShotPreviewPatch(701, LongShotService.Crop(page, 2000, 1001), 440, 3001, 3));
        Apply(new LongShotPreviewPatch(1001, replacement, 440, 3001, 3));
        var expected = LongShotService.Stack([LongShotService.Crop(page, 0, 2000)!, replacement], 440)!;
        var reference = new LongShotLivePreviewWindow.ThumbnailBuffer().Apply(
            new LongShotPreviewPatch(0, expected, 440, expected.Height, 3, Reset: true));
        var actual = ReadThumbnails(stripes);
        var want = ReadThumbnails(reference.Sources);
        Check(actual.Height == 1501 && actual.Width == 220 && actual.Pixels.AsSpan().SequenceEqual(want.Pixels),
            "奇数裁尾或刷新造成缩略图累计偏移、缺行或旧像素残留");
        Check(stripes.All(s => s.IsFrozen && s.PixelHeight <= 512), "预览条带没有冻结或超过安全纹理高度");
        var reset = DetailedPage(173, 1303);
        Apply(new LongShotPreviewPatch(0, reset, reset.Width, reset.Height, 1, Reset: true));
        Check(ReadThumbnails(stripes).Pixels.AsSpan().SequenceEqual(reset.Pixels), "视口重置保留了旧宽度或旧条带");
        Apply(new LongShotPreviewPatch(reset.Height, null, reset.Width, 0, 1));
        Check(stripes.Count == 0, "裁空尾部后仍留有条带");

        void Apply(LongShotPreviewPatch patch)
        {
            var update = buffer.Apply(patch);
            while (stripes.Count > update.FirstChangedStripe) stripes.RemoveAt(stripes.Count - 1);
            stripes.AddRange(update.Sources);
        }
    }

    static void LivePreviewWindowProbe()
    {
        var settings = SettingsService.Instance.Current;
        var foreground = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var cursor);
        var work = ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos());
        var target = new Window
        {
            Content = new Border { Background = Brushes.White }, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = work.Left + 40, Top = work.Top + 40, Width = 280, Height = 320,
        };
        try
        {
            target.Show(); Layout(target); FocusTarget(target);
            var dpi = VisualTreeHelper.GetDpi(target);
            var origin = target.PointToScreen(new Point());
            var region = new RECT { Left = (int)origin.X, Top = (int)origin.Y,
                Right = (int)(origin.X + target.ActualWidth * dpi.DpiScaleX),
                Bottom = (int)(origin.Y + target.ActualHeight * dpi.DpiScaleY) };
            var targetHandle = new WindowInteropHelper(target).Handle;
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeService.ApplyTheme(theme);
                var preview = new LongShotLivePreviewWindow(region);
                try
                {
                    Check(preview.TryShowOutside(), "测试窗口侧边有空间却无法显示实时预览");
                    Layout(preview);
                    var tl = ScreenHelper.ToDip(new POINT { X = region.Left, Y = region.Top }, preview);
                    var br = ScreenHelper.ToDip(new POINT { X = region.Right, Y = region.Bottom }, preview);
                    Check(!new Rect(preview.Left, preview.Top, preview.ActualWidth, preview.ActualHeight).IntersectsWith(new Rect(tl, br)),
                        "实际预览窗口压入被抓选区");
                    var ex = Win32.GetWindowLong(new WindowInteropHelper(preview).Handle, Win32.GWL_EXSTYLE);
                    Check((ex & Win32.WS_EX_NOACTIVATE) != 0 && (ex & Win32.WS_EX_TOOLWINDOW) != 0
                        && !preview.ShowActivated && !preview.Focusable && Win32.GetForegroundWindow() == targetHandle,
                        "预览没有设置不激活样式或显示时抢了焦点");
                    var page = DetailedPage(440, 5000);
                    preview.Enqueue(new LongShotPreviewPatch(0, LongShotService.Crop(page, 0, 1401), 440, 1401, 1, Reset: true));
                    preview.Enqueue(new LongShotPreviewPatch(0, LongShotService.Crop(page, 1401, 1300), 440, 2701, 2));
                    preview.Enqueue(new LongShotPreviewPatch(701, LongShotService.Crop(page, 2000, 1001), 440, 3001, 3));
                    WaitFor(() => Descendants<TextBlock>(preview).Any(t => t.Text.Contains("440×3001")), target, "实时预览后台队列没有完成增量");
                    Layout(preview);
                    var list = Descendants<ListBox>(preview).Single(l => l.Name == "LongShotLiveTiles");
                    var sources = list.Items.Cast<LongShotLivePreviewWindow.PreviewStripe>().Select(s => s.Source).ToArray();
                    var reference = new LongShotLivePreviewWindow.ThumbnailBuffer().Apply(new LongShotPreviewPatch(0,
                        LongShotService.Crop(page, 0, 3001), 440, 3001, 3, Reset: true));
                    Check(ReadThumbnails(sources).Pixels.AsSpan().SequenceEqual(ReadThumbnails(reference.Sources).Pixels),
                        "WPF 收到的增量条带与拼接内容不同");
                    Check(Descendants<Image>(list).Count() > 0 && Descendants<Image>(list).Count() < list.Items.Count,
                        "实时预览没有虚拟化可见条带");
                    var scroll = Descendants<ScrollViewer>(list).First();
                    Check(scroll.VerticalOffset > 0 && scroll.ExtentHeight - scroll.ViewportHeight - scroll.VerticalOffset < 2,
                        "预览更新后没有滚到最新尾部");
                    SaveHud(preview, $"longshot-live-{theme}.png");
                    var point = preview.PointToScreen(new Point(20, 20));
                    Win32.SetCursorPos((int)point.X, (int)point.Y);
                    INPUT[] click = [MouseInput(0x0002), MouseInput(0x0004)];
                    Check(Win32.SendInput(2, click, Marshal.SizeOf<INPUT>()) == 2, "不能向预览发送真实点击");
                    Layout(target);
                    Check(Win32.GetForegroundWindow() == targetHandle, "点击实时预览抢走目标窗口焦点");
                    preview.Hide();
                    var reset = DetailedPage(240, 90);
                    preview.Enqueue(new LongShotPreviewPatch(0, reset, 240, 90, 1, Reset: true));
                    WaitFor(() => Descendants<TextBlock>(preview).Any(t => t.Text.Contains("240×90")), target, "隐藏期间未接收预览重置");
                    Check(preview.TryShowOutside(), "隐藏后无法重新显示预览"); Layout(preview);
                    Check(list.Items.Count == 1 && Win32.GetForegroundWindow() == targetHandle, "重新显示残留旧条带或抢了焦点");
                }
                finally
                {
                    preview.Close();
                    WaitFor(() => preview.Completion.IsCompleted, target, "预览关闭后后台队列未退出");
                }
            }

            var monitor = Win32.MonitorFromWindow(targetHandle, Win32.MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            Check(Win32.GetMonitorInfo(monitor, ref info), "无法取得全屏预览测试区域");
            var full = new LongShotLivePreviewWindow(info.rcMonitor);
            try { Check(!full.TryShowOutside() && !full.IsVisible, "全屏选区仍显示了污染截图的实时预览"); }
            finally { full.Close(); WaitFor(() => full.Completion.IsCompleted, target, "隐藏预览的后台队列未退出"); }

            var failed = new LongShotLivePreviewWindow(region);
            try
            {
                failed.Enqueue(new LongShotPreviewPatch(0, null, 0, 0, 0, Reset: true));
                WaitFor(() => failed.Completion.IsCompleted, target, "坏预览增量没有结束后台处理");
                Check(!failed.Completion.IsFaulted, "后台预览异常没有被记录和隔离");
                var queue = (System.Threading.Channels.Channel<LongShotPreviewPatch>)typeof(LongShotLivePreviewWindow)
                    .GetField("_queue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(failed)!;
                Check(queue.Reader.Completion.IsCompleted, "后台失败后仍接收预览，可能无限积压原图");
                var image = DetailedPage(240, 90);
                failed.Enqueue(new LongShotPreviewPatch(0, image, 240, 90, 1, Reset: true));
                Check(!queue.Writer.TryWrite(new LongShotPreviewPatch(0, image, 240, 90, 1, Reset: true)),
                    "预览失败后队列仍接收新分片");
            }
            finally { failed.Close(); }
        }
        finally
        {
            target.Close(); ThemeService.Apply(settings);
            Win32.SetCursorPos(cursor.X, cursor.Y);
            if (foreground != IntPtr.Zero) Win32.SetForegroundWindow(foreground);
        }
    }

    static void PreviewFitSettingProbe()
    {
        var original = SettingsService.Instance.Current;
        try
        {
            var settings = AppSettings.CreateDefault();
            settings.LongShotPreviewWidth = 480;
            settings.LongShotPreviewHeight = 420;
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var fit in new[] { false, true })
            {
                settings.LongShotPreviewFitWidth = fit;
                SettingsService.Instance.Apply(settings);
                ThemeService.ApplyTheme(theme);
                foreach (var explicitOverride in new[] { false, true })
                {
                    var expectedFit = explicitOverride ? !fit : fit;
                    var window = new LongShotWindow(DetailedPage(1200, 2400), "默认视图设置",
                        explicitOverride ? !fit : null) { ShowActivated = false, ShowInTaskbar = false };
                    try
                    {
                        window.Show(); Layout(window);
                        var scroll = Descendants<ScrollViewer>(window).First();
                        Check(expectedFit ? scroll.ExtentWidth <= scroll.ViewportWidth + 2 : scroll.ExtentWidth > scroll.ViewportWidth,
                            "完成预览未遵循设置或可选参数的适应宽度选择");
                        if (!expectedFit)
                        {
                            var dpi = VisualTreeHelper.GetDpi(window);
                            var tile = Descendants<Image>(window).First();
                            Check(Math.Abs(tile.ActualWidth * dpi.DpiScaleX - 1200) <= 1, "默认原始大小不是真正 100% 物理像素");
                        }
                        if (!explicitOverride) SaveHud(window, $"longshot-view-{theme}-{(fit ? "fit" : "original")}.png");
                    }
                    finally { window.Close(); }
                }
            }
        }
        finally { SettingsService.Instance.Apply(original); ThemeService.Apply(original); }
    }

    static void CompletionActionProbe()
    {
        var original = SettingsService.Instance.Current;
        var foreground = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var cursor);
        var work = ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos());
        var target = new Window { Content = new Border { Background = Brushes.White },
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = work.Left + 40, Top = work.Top + 80,
            Width = 280, Height = 340 };
        try
        {
            target.Show(); Layout(target); FocusTarget(target);
            var origin = target.PointToScreen(new Point(10, 10));
            var region = new RECT { Left = (int)origin.X, Top = (int)origin.Y,
                Right = (int)origin.X + W, Bottom = (int)origin.Y + H };
            foreach (var (showPreview, action, wantsPreview) in new[]
                { (true, CaptureAction.Pin, true), (false, CaptureAction.Pin, false), (false, CaptureAction.None, true) })
            {
                var settings = AppSettings.CreateDefault();
                settings.LongShotShowPreview = showPreview;
                settings.LongShotLivePreview = false;
                settings.CaptureEnterAction = action;
                SettingsService.Instance.Apply(settings);
                var before = Application.Current.Windows.Cast<Window>().ToHashSet();
                var method = typeof(AppHost).GetMethod("LongShotAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var task = (Task)method.Invoke(new AppHost(), [region])!;
                try
                {
                    WaitFor(() => Application.Current.Windows.OfType<LongShotHud>().Any(h =>
                        !before.Contains(h) && Descendants<Button>(h).Single(b => b.Name == "LongShotMode").IsEnabled),
                        target, "宿主长截图首帧未就绪");
                    var hud = Application.Current.Windows.OfType<LongShotHud>().Single(h => !before.Contains(h));
                    Descendants<Button>(hud).Single(b => b.Name == "LongShotFinish").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    WaitFor(() => task.IsCompleted, target, "宿主长截图没有完成收尾");
                    task.GetAwaiter().GetResult();
                    var created = Application.Current.Windows.Cast<Window>().Where(w => !before.Contains(w)).ToArray();
                    Check(created.OfType<LongShotWindow>().Count() == (wantsPreview ? 1 : 0)
                        && created.OfType<PinnedShotWindow>().Count() == (wantsPreview ? 0 : 1),
                        "完成预览开关或 None 动作的兜底路径不正确");
                    Check(!created.OfType<LongShotHud>().Any() && !created.OfType<LongShotLivePreviewWindow>().Any(),
                        "宿主完成后仍留有浮条或后台预览窗");
                }
                finally
                {
                    foreach (var hud in Application.Current.Windows.OfType<LongShotHud>().Where(h => !before.Contains(h)).ToArray())
                        Descendants<Button>(hud).Single(b => b.Name == "LongShotFinish").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (!task.IsCompleted) WaitFor(() => task.IsCompleted, target, "宿主长截图测试清理超时");
                    foreach (var window in Application.Current.Windows.Cast<Window>().Where(w => !before.Contains(w)).ToArray()) window.Close();
                }
            }
        }
        finally
        {
            target.Close(); SettingsService.Instance.Apply(original); ThemeService.Apply(original);
            Win32.SetCursorPos(cursor.X, cursor.Y);
            if (foreground != IntPtr.Zero) Win32.SetForegroundWindow(foreground);
        }
    }

    static void HudProbe()
    {
        try
        {
            foreach (var theme in new[] { FlashTrans.Core.AppTheme.Dark, FlashTrans.Core.AppTheme.Light })
            {
                ThemeService.ApplyTheme(theme);
                var previewToggles = new List<bool>();
                var region = new RECT { Left = 80, Top = 80, Right = 700, Bottom = 500 };
                var hud = new LongShotHud(region, "F2", livePreviewOn: true, onLivePreviewToggled: previewToggles.Add);
                try
                {
                    hud.Show(); Layout(hud);
                    var mode = Descendants<Button>(hud).Single(b => b.Name == "LongShotMode");
                    Check(!hud.Automatic && !mode.IsEnabled, "初始状态没有等待手动滚动或首帧未准备好就能自动滚动");
                    hud.Report(300, 1); Layout(hud);
                    Check(mode.IsEnabled, "首帧完成后未启用切换");
                    SaveHud(hud, $"longshot-hud-{theme}-manual.png");
                    mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(hud);
                    Check(hud.Automatic && !hud.Paused && !hud.Cancelled && mode.Content is string playing
                        && playing.StartsWith("暂停滚动"), "切换自动时误结束或文案不正确");
                    SaveHud(hud, $"longshot-hud-{theme}-auto.png");
                    mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(hud.Paused && !hud.Automatic, "自动按钮没有进入真正的暂停状态");
                    hud.UseManualScroll();
                    hud.ReportAlignmentLost(true); Layout(hud);
                    Check(!hud.Automatic && Descendants<TextBlock>(hud).Any(t => t.Text.Contains("重叠区域"))
                        && !Descendants<TextBlock>(hud).Any(t => t.Text.Contains("回滚")), "手动失配提示仍武断地要求回滚");
                    SaveHud(hud, $"longshot-hud-{theme}-recover.png");
                    hud.PauseAutomatic(LongShotStop.Diverged); Layout(hud);
                    Check(!hud.Automatic && !hud.Paused && !hud.Cancelled && mode.Content is string stopped && stopped.StartsWith("自动滚动")
                        && Descendants<TextBlock>(hud).Any(t => t.Text.Contains("已切到手动")), "自动暂停未保留手动继续入口");
                    SaveHud(hud, $"longshot-hud-{theme}-paused.png");
                    Descendants<Button>(hud).Single(b => b.Name == "LongShotPreview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!hud.LivePreviewVisible && previewToggles.SequenceEqual(new[] { false }), "浮条预览按钮未切换状态");
                    foreach (var width in new[] { 360d, 768d, 1280d })
                    {
                        hud.SizeToContent = SizeToContent.Height;
                        hud.Width = width;
                        hud.UseManualScroll();
                        for (var state = 0; state < 3; state++)
                        {
                            if (state > 0) hud.ToggleScroll();
                            Layout(hud);
                            VerifyBounds(hud, width);
                            SaveHud(hud, $"longshot-hud-{theme}-{width}-{state}.png");
                        }
                    }
                    Descendants<Button>(hud).Single(b => b.Name == "LongShotFinish")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(hud.Cancelled, "完成按钮未结束截图");
                }
                finally { hud.Close(); }
                var longKey = new LongShotHud(region, "Ctrl+Alt+Shift+Win+F12", livePreviewOn: true,
                    onLivePreviewToggled: _ => { });
                try
                {
                    longKey.Show(); longKey.Report(60000, 200);
                    foreach (var width in new[] { 360d, 768d, 1280d })
                    {
                        longKey.SizeToContent = SizeToContent.Height; longKey.Width = width;
                        longKey.ToggleScroll(); Layout(longKey); VerifyBounds(longKey, width);
                        SaveHud(longKey, $"longshot-hud-{theme}-{width}-longkey.png");
                    }
                }
                finally { longKey.Close(); }
            }
        }
        finally { ThemeService.ApplyTheme(FlashTrans.Core.AppTheme.Dark); }

        static void VerifyBounds(LongShotHud hud, double width)
        {
            foreach (var button in Descendants<Button>(hud).Where(b => b.Visibility == Visibility.Visible))
            {
                var bounds = button.TransformToAncestor(hud).TransformBounds(new Rect(button.RenderSize));
                Check(bounds.Left >= 0 && bounds.Right <= hud.ActualWidth + 0.1 && bounds.Bottom <= hud.ActualHeight + 0.1,
                    $"浮条 {width} 宽时按钮溢出：{button.Name}");
            }
        }
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
        var shift = LongShotService.FindShift(prev, next);
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
        var settings = SettingsService.Instance.Current;
        var originalWidth = settings.LongShotPreviewWidth;
        var originalHeight = settings.LongShotPreviewHeight;
        var page = DetailedPage(513, 40017);
        var window = new LongShotWindow(page, "原始像素测试", fitWidth: true)
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
            Check(scroll.ExtentWidth <= scroll.ViewportWidth + 2, "首次打开预览没有适应宽度");
            window.SetPreviewZoom(1); Layout(window);

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
        finally
        {
            window.Close();
            settings.LongShotPreviewWidth = originalWidth;
            settings.LongShotPreviewHeight = originalHeight;
            SettingsService.Instance.Save();
        }
    }

    static void PreviewWindowSizeProbe()
    {
        var settings = SettingsService.Instance.Current;
        var originalWidth = settings.LongShotPreviewWidth;
        var originalHeight = settings.LongShotPreviewHeight;
        try
        {
            settings.LongShotPreviewWidth = 760;
            settings.LongShotPreviewHeight = 540;
            var image = DetailedPage(1800, 3200);
            foreach (var theme in new[] { FlashTrans.Core.AppTheme.Light, FlashTrans.Core.AppTheme.Dark })
            {
                ThemeService.ApplyTheme(theme);
                var window = new LongShotWindow(image, "宽图预览 Wide image", fitWidth: true) { ShowActivated = false };
                try
                {
                    window.Show(); Layout(window);
                    Check(Math.Abs(window.Width - 760) < 1 && Math.Abs(window.Height - 540) < 1, "没有恢复上次窗口尺寸");
                    foreach (var size in new[] { (360d, 320d), (760d, 540d) })
                    {
                        window.Width = size.Item1; window.Height = size.Item2; Layout(window);
                        var scroll = Descendants<ScrollViewer>(window).First();
                        Check(scroll.ExtentWidth <= scroll.ViewportWidth + 2, "调整窗口后宽图仍被横向截断");
                    }
                    SaveHud(window, $"longshot-fit-{theme}.png");
                    window.WindowState = WindowState.Maximized; Layout(window);
                    var full = Descendants<ScrollViewer>(window).First();
                    Check(full.ExtentWidth <= full.ViewportWidth + 2, "最大化后预览没有适应宽度");
                }
                finally { window.Close(); }
                SettingsService.Instance.Load();
                Check(Math.Abs(SettingsService.Instance.Current.LongShotPreviewWidth - 760) < 1
                    && Math.Abs(SettingsService.Instance.Current.LongShotPreviewHeight - 540) < 1,
                    "最大化关闭后错误地保存了全屏尺寸，或尺寸未写入配置");
                SettingsService.Instance.Apply(settings);
            }
        }
        finally
        {
            settings.LongShotPreviewWidth = originalWidth;
            settings.LongShotPreviewHeight = originalHeight;
            SettingsService.Instance.Apply(settings);
            ThemeService.Apply(settings);
        }
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

    static void RealScrollPixelsProbe(int footerHeight = 0, bool animated = false)
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
        var animation = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        var video = new Border();
        var animationTicks = 0;
        animation.Tick += (_, _) =>
        {
            animationTicks++;
            video.Background = new SolidColorBrush(Color.FromRgb((byte)(30 + animationTicks % 5 * 40), 80, 180));
        };
        try
        {
            window.Show(); Layout(window);
            var dpi = VisualTreeHelper.GetDpi(window);
            picture.Width = page.Width / dpi.DpiScaleX;
            picture.Height = page.Height / dpi.DpiScaleY;
            if (animated)
            {
                scroll.Content = null;
                var document = new Grid { Width = picture.Width, Height = picture.Height };
                document.Children.Add(picture);
                video.Width = (W - 48) / dpi.DpiScaleX;
                video.Height = 170 / dpi.DpiScaleY;
                video.HorizontalAlignment = HorizontalAlignment.Left;
                video.VerticalAlignment = VerticalAlignment.Top;
                video.Margin = new Thickness(24 / dpi.DpiScaleX, 180 / dpi.DpiScaleY, 0, 0);
                video.Background = new SolidColorBrush(Color.FromRgb(30, 80, 180));
                document.Children.Add(video);
                scroll.Content = document;
                animation.Start();
            }
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
            var initial = Frame(page, 0);
            // WPF 布局完成不代表 DWM 已合成；先确认真实首屏，避免把上一窗口的残影当成正文。
            WaitFor(() =>
            {
                var visible = capture.Grab();
                if (visible is null) return false;
                if (!animated) return visible.Pixels.AsSpan(0, (H - footerHeight) * initial.Stride)
                    .SequenceEqual(initial.Pixels.AsSpan(0, (H - footerHeight) * initial.Stride));
                for (var y = 0; y < H - footerHeight; y++)
                    for (var x = 0; x < W; x++)
                    {
                        if (y is >= 180 and < 350 && x is >= 24 and < W - 24) continue;
                        var at = y * initial.Stride + x * 4;
                        if (!visible.Pixels.AsSpan(at, 4).SequenceEqual(initial.Pixels.AsSpan(at, 4))) return false;
                    }
                return true;
            }, window, "真实测试首屏尚未显示，不能开始像素拼接验证");
            var offset = 0;
            CapturedImage? firstObserved = null;
            var task = Task.Run(() => LongShotService.RunForTestAsync(region, _ =>
                {
                    var image = capture.Grab();
                    firstObserved ??= image;
                    return image;
                },
                _ => window.Dispatcher.Invoke(() => scroll.ScrollToVerticalOffset((offset = Math.Min(600, offset + 90)) / dpi.DpiScaleY)),
                cancelled: () => stop.IsCancellationRequested,
                delay: _ => Task.Delay(15)));
            while (!task.IsCompleted) { Layout(window); Thread.Sleep(5); }
            var result = task.GetAwaiter().GetResult();
            Check(result.Stopped == LongShotStop.Bottom && result.Image is not null && result.Frames > 1,
                $"真实滚动未完成：{result.Stopped}，{result.Frames} 屏");
            var expected = LongShotService.Crop(page, 0, page.Height - footerHeight)!;
            Console.WriteLine($"       真实滚动输出 {result.Image!.Width}×{result.Image.Height}，预期 {expected.Width}×{expected.Height}，{result.Frames} 段，滚动位置 {offset}");
            if (footerHeight == 0 && !animated)
            {
                var firstMatches = firstObserved is not null && firstObserved.Pixels.AsSpan().SequenceEqual(Frame(page, 0).Pixels);
                Console.WriteLine($"       初屏与测试正文逐像素一致：{firstMatches}");
                var args = Environment.GetCommandLineArgs();
                var previewIndex = Array.IndexOf(args, "--preview-dir");
                if (previewIndex >= 0 && previewIndex + 1 < args.Length)
                    firstObserved?.SavePng(Path.Combine(args[previewIndex + 1], "longshot-real-first.png"));
            }
            Check(result.Image.Width == page.Width && result.Image.Height == expected.Height,
                $"真实滚动拼接尺寸不正确：{result.Image.Width}×{result.Image.Height}，预期 {expected.Width}×{expected.Height}");
            if (animated)
            {
                Check(animationTicks >= 10, "实际动画没有持续播放，测试无效");
                var color = result.Image.Pixels[180 * result.Image.Stride + 24 * 4];
                for (var y = 0; y < result.Image.Height; y++)
                    for (var x = 0; x < W; x++)
                    {
                        var i = y * result.Image.Stride + x * 4;
                        if (y is >= 180 and < 350 && x is >= 24 and < W - 24)
                            Check(result.Image.Pixels[i] == color, "真实动画被拼成不同时刻的条带");
                        else Check(result.Image.Pixels.AsSpan(i, 4).SequenceEqual(expected.Pixels.AsSpan(i, 4)),
                            $"真实动态截图的正文在 {x},{y} 错位");
                    }
                Console.WriteLine($"       动画变化 {animationTicks} 次，输出 {result.Image.Height} 行，静态正文逐像素一致，动画单帧完整");
            }
            else
            {
                Check(result.Image.Pixels.AsSpan().SequenceEqual(expected.Pixels), "真实抓屏或拼接发生缩放、模糊、底栏残留或像素错位");
                Console.WriteLine($"       实际底部浮层 {footerHeight} 像素，输出 {result.Image.Height} 行与原始正文逐像素一致");
            }
        }
        finally { animation.Stop(); stop.Cancel(); window.Close(); }
    }

    static void RealManualScrollProbe(bool nested = false)
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
        var stitched = 1;
        try
        {
            window.Show(); window.Activate(); Layout(window);
            var dpi = VisualTreeHelper.GetDpi(window);
            picture.Width = W / dpi.DpiScaleX;
            picture.Height = page.Height / dpi.DpiScaleY;
            scroll.Width = W / dpi.DpiScaleX + 24;
            scroll.Height = H / dpi.DpiScaleY;
            Layout(window);
            FrameworkElement captureSurface = picture;
            var captureWidth = W;
            var captureHeight = H;
            if (nested)
            {
                const int outerWidth = 400, outerHeight = 700;
                var pixels = new byte[outerWidth * outerHeight * 4];
                for (var y = 0; y < outerHeight; y++)
                    for (var x = 0; x < outerWidth; x++)
                    {
                        var i = (y * outerWidth + x) * 4;
                        var color = RowColor((uint)(y + 5000));
                        pixels[i] = (byte)color; pixels[i + 1] = (byte)(color >> 8);
                        pixels[i + 2] = (byte)(color >> 16); pixels[i + 3] = 255;
                    }
                var background = new Image { Source = new CapturedImage(outerWidth, outerHeight, pixels).ToBitmap(), Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(background, BitmapScalingMode.NearestNeighbor);
                var surface = new Grid { Width = outerWidth / dpi.DpiScaleX, Height = outerHeight / dpi.DpiScaleY };
                var border = (Border)window.Content;
                border.Child = null;
                border.Padding = new Thickness(0);
                surface.Children.Add(background);
                scroll.HorizontalAlignment = HorizontalAlignment.Left;
                scroll.VerticalAlignment = VerticalAlignment.Top;
                scroll.Margin = new Thickness(70 / dpi.DpiScaleX, 70 / dpi.DpiScaleY, 0, 0);
                scroll.Padding = new Thickness(0);
                scroll.BorderThickness = new Thickness(0);
                scroll.Width = W / dpi.DpiScaleX + SystemParameters.VerticalScrollBarWidth;
                surface.Children.Add(scroll);
                var outer = new ScrollViewer
                {
                    Content = surface, CanContentScroll = false, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                };
                border.Child = outer;
                window.Width = outerWidth / dpi.DpiScaleX + SystemParameters.VerticalScrollBarWidth;
                window.Height = 500 / dpi.DpiScaleY;
                Layout(window);
                var viewport = Descendants<ScrollContentPresenter>(scroll).Single();
                scroll.Width += W / dpi.DpiScaleX - viewport.ActualWidth;
                Layout(window);
                Check(outer.ScrollableHeight > 0 && scroll.ScrollableHeight > 0, "未形成真实双层可滚动控件");
                captureSurface = outer;
                captureWidth = (int)Math.Round(outer.ActualWidth * dpi.DpiScaleX);
                captureHeight = (int)Math.Round(outer.ActualHeight * dpi.DpiScaleY);
                SaveHud(window, "longshot-nested-controls.png");
            }
            var origin = picture.PointToScreen(new Point(0, 0));
            var captureOrigin = captureSurface.PointToScreen(new Point(0, 0));
            var region = new RECT
            {
                Left = (int)Math.Round(captureOrigin.X), Top = (int)Math.Round(captureOrigin.Y),
                Right = (int)Math.Round(captureOrigin.X) + captureWidth, Bottom = (int)Math.Round(captureOrigin.Y) + captureHeight,
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
            Check(!hud.Automatic && hud.Paused && !hud.Cancelled, "真实点击暂停没有停住自动滚动");
            Click(Descendants<Button>(hud).Single(b => b.Name == "LongShotManual"));
            Check(!hud.Automatic && !hud.Paused && !hud.Cancelled, "真实点击手动滚动没有切回模式");
            Check(Win32.GetForegroundWindow() == targetHandle,
                $"浮条按钮抢走了被截窗口的焦点：目标={targetHandle}，浮条={new System.Windows.Interop.WindowInteropHelper(hud).Handle}，实际={Win32.GetForegroundWindow()}");
            Win32.SetCursorPos((int)origin.X + W / 2, (int)origin.Y + H / 2);
            task = Task.Run(() => LongShotService.RunAsync(region,
                onProgress: (value, frames) =>
                {
                    Volatile.Write(ref height, value);
                    Volatile.Write(ref stitched, frames);
                    window.Dispatcher.Invoke(() => hud.Report(value, frames));
                },
                cancelled: () => stop.IsCancellationRequested || hud.Cancelled, automatic: () => hud.Automatic,
                paused: () => hud.Paused));
            Wait(() => Volatile.Read(ref height) == captureHeight, "手动截图首帧没有就绪");
            SendMouse(Win32.MOUSEEVENTF_WHEEL, unchecked((uint)-Win32.WHEEL_DELTA));
            Wait(() => Volatile.Read(ref stitched) > 1, "真实鼠标滚轮没有拼出新内容");

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
                $"真实手动滚动拼接发生缺行、重复或像素变化：{result.Image?.Width}×{result.Image?.Height}");
            Console.WriteLine($"       {(nested ? "双层嵌套" : "普通页面")}手动滚轮 + 拖动滚动条：{result.Frames} 段，{expectedHeight} 行逐像素一致");
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

    sealed class PreviewCollector
    {
        CapturedImage? _image;
        Exception? _failure;
        public int Resets { get; private set; }
        public int Refreshes { get; private set; }
        public int TrimmedRows { get; private set; }

        public void Accept(LongShotPreviewPatch patch)
        {
            try
            {
                if (patch.Reset) { _image = null; Resets++; }
                var oldHeight = _image?.Height ?? 0;
                Check(patch.TrimTailRows <= oldHeight, "预览增量裁掉了不存在的尾行");
                var kept = oldHeight - patch.TrimTailRows;
                var added = patch.AddedSlice?.Height ?? 0;
                if (patch.TrimTailRows == added && patch.TrimTailRows > 0) Refreshes++;
                TrimmedRows += patch.TrimTailRows;
                Check(kept + added == patch.TotalHeight, "预览增量总高度错误");
                var pixels = new byte[patch.TotalWidth * patch.TotalHeight * 4];
                if (kept > 0) _image!.Pixels.AsSpan(0, kept * _image.Stride).CopyTo(pixels);
                if (patch.AddedSlice is { } slice) slice.Pixels.CopyTo(pixels, kept * patch.TotalWidth * 4);
                _image = new CapturedImage(patch.TotalWidth, patch.TotalHeight, pixels);
            }
            catch (Exception ex) { _failure ??= ex; }
        }

        public void Verify(LongShotResult result)
        {
            if (_failure is not null) throw new InvalidOperationException("预览增量回放失败", _failure);
            Check(Resets > 0 && _image is not null && result.Image is not null
                && _image.Width == result.Image.Width && _image.Height == result.Image.Height
                && _image.Pixels.AsSpan().SequenceEqual(result.Image.Pixels), "预览增量与最终原图逐像素不一致");
        }
    }

    static CapturedImage ReadThumbnails(IEnumerable<BitmapSource> source)
    {
        var bitmaps = source.ToArray();
        Check(bitmaps.Length > 0, "缩略图没有条带");
        var width = bitmaps[0].PixelWidth;
        var height = bitmaps.Sum(b => b.PixelHeight);
        var pixels = new byte[width * height * 4];
        var offset = 0;
        foreach (var bitmap in bitmaps)
        {
            Check(bitmap.IsFrozen && bitmap.PixelWidth == width && bitmap.PixelHeight <= LongShotLivePreviewWindow.StripeHeight,
                "缩略条带尺寸、冻结或宽度不一致");
            bitmap.CopyPixels(pixels, width * 4, offset);
            offset += width * bitmap.PixelHeight * 4;
        }
        return new CapturedImage(width, height, pixels);
    }

    static HotkeySpec AvailableToggleHotkey()
    {
        using var message = new MessageWindow("LongShot.Toggle.Available");
        foreach (var index in Enumerable.Range(1, 12))
        {
            // 单键截图的低级钩子也会收到带修饰键的合成输入，测试不选已经占用的那个主键。
            var plain = HotkeySpec.Parse("F" + index);
            if (!Win32.RegisterHotKey(message.Handle, 0x7162, plain.Win32Modifiers, plain.VirtualKey))
            {
                Console.WriteLine($"       跳过已占用的单键 {plain}");
                continue;
            }
            Win32.UnregisterHotKey(message.Handle, 0x7162);
            var spec = HotkeySpec.Parse("Ctrl+Alt+F" + index);
            if (!Win32.RegisterHotKey(message.Handle, 0x7160, spec.Win32Modifiers, spec.VirtualKey)) continue;
            Win32.UnregisterHotKey(message.Handle, 0x7160);
            return spec;
        }
        throw new InvalidOperationException("没有可用于长截图真实热键测试的空闲组合键");
    }

    static void InjectHotkey(HotkeySpec spec, Window target, int repeats = 1)
    {
        var keys = new List<ushort>();
        if (spec.Modifiers.HasFlag(ModifierKeys.Control)) keys.Add(Win32.VK_CONTROL);
        if (spec.Modifiers.HasFlag(ModifierKeys.Alt)) keys.Add(Win32.VK_MENU);
        if (spec.Modifiers.HasFlag(ModifierKeys.Shift)) keys.Add(Win32.VK_SHIFT);
        if (spec.Modifiers.HasFlag(ModifierKeys.Windows)) keys.Add(Win32.VK_LWIN);
        var inputs = keys.Select(k => KeyboardInput(k, false)).ToList();
        inputs.AddRange(Enumerable.Repeat(KeyboardInput((ushort)spec.VirtualKey, false), repeats));
        try
        {
            Check(Win32.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()) == inputs.Count,
                "无法向测试目标发送完整热键");
            // 按下和松开分别过消息循环，避免零时长合成输入与 MOD_NOREPEAT 的释放处理竞态。
            Layout(target);
        }
        finally
        {
            var release = new List<INPUT> { KeyboardInput((ushort)spec.VirtualKey, true) };
            release.AddRange(keys.AsEnumerable().Reverse().Select(k => KeyboardInput(k, true)));
            Check(Win32.SendInput((uint)release.Count, release.ToArray(), Marshal.SizeOf<INPUT>()) == release.Count,
                "测试热键未完整松开");
            Layout(target);
        }
    }

    static INPUT KeyboardInput(ushort key, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = key, dwFlags = up ? Win32.KEYEVENTF_KEYUP : 0 } },
    };

    static INPUT MouseInput(uint flags) => new()
    {
        type = Win32.INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } },
    };

    static void FocusTarget(Window window)
    {
        // 后台自测仅调用 Activate 可能被系统前台锁拒绝；真实点击自己的测试窗口后再验证焦点。
        var point = window.PointToScreen(new Point(window.ActualWidth / 2, window.ActualHeight / 2));
        var target = new WindowInteropHelper(window).Handle;
        var position = new POINT { X = (int)point.X, Y = (int)point.Y };
        WaitFor(() => Win32.GetAncestor(Win32.WindowFromPoint(position), Win32.GA_ROOT) == target,
            window, "测试目标被其他窗口遮住，不能向它发送焦点验证输入");
        Win32.SetCursorPos((int)point.X, (int)point.Y);
        INPUT[] click = [MouseInput(0x0002), MouseInput(0x0004)];
        var released = false;
        MouseButtonEventHandler onReleased = (_, _) => released = true;
        window.PreviewMouseLeftButtonUp += onReleased;
        try
        {
            Check(Win32.SendInput(2, click, Marshal.SizeOf<INPUT>()) == 2, "不能激活测试目标窗口");
            WaitFor(() => released, window, "焦点点击尚未松开，不能显示浮条或预览");
        }
        finally { window.PreviewMouseLeftButtonUp -= onReleased; }
        WaitFor(() => Win32.GetForegroundWindow() == target, window,
            $"测试目标未取得焦点：目标={target}，实际前台={Win32.GetForegroundWindow()}");
    }

    static void WaitFor(Func<bool> done, Window window, string message)
    {
        var until = Environment.TickCount64 + 5000;
        while (!done() && Environment.TickCount64 < until) { Layout(window); Thread.Sleep(5); }
        Check(done(), message);
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
