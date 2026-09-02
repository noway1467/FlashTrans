using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

/// <summary>
/// 录制动图。
///
/// GIF 那条是自己拼字节流的（每帧单独编，再把调色板和 LZW 码流接起来），
/// 这种代码「编出来的文件能不能播」光看断言看不出来，所以这里编完之后
/// 一律解回来验：帧数对不对、每帧的画面有没有错位、延时和循环标记在不在。
/// 帧序错乱和延时丢失是这类拼接最容易犯的两个错，各有一项专门盯着。
/// </summary>
static class RecordProbe
{
    public static void RunAll(Action<string, Action> step)
    {
        step("录制：帧延时按帧率换算", DelayMathProbe);
        step("录制：实测帧率按帧间隔算", EffectiveFpsProbe);
        step("录制：动图 GIF 能解回来，帧数和尺寸都对", GifRoundTripProbe);
        step("录制：每帧画面对得上，没有错位或重复", GifFrameOrderProbe);
        step("录制：写了循环标记，不会只播一遍", GifLoopProbe);
        step("录制：每帧都带延时，且等于设定帧率", GifDelayProbe);
        step("录制：不是 GIF 的字节不会被当成帧解析", GifJunkProbe);
        step("录制：真抓一段屏幕，帧数和实测帧率合理", RealRecordProbe);
        step("录制：真编动图 WebP，容器里 VP8X/ANIM/ANMF 都对", WebpRealProbe);
        step("录制：没有 img2webp 时老实退回 GIF", WebpFallbackProbe);
        step("录制：同样内容 WebP 比 GIF 小", SizeCompareProbe);
        step("录制：帧率和时长的夹取范围", ClampProbe);
        step("录制：浮条能构造，进度和编码提示都会更新", HudProbe);
        step("录制：工具条多了「录制」还塞得进常见屏宽", ToolbarWidthProbe);
        step("录制：Esc 要松开过一次才算停，不会开录就被判停", EscArmProbe);
        step("录制：暂停期间不抓帧，恢复后接着录", PauseProbe);
        step("录制：暂停掉的时间不算进时长，回放是连着的", PauseClockProbe);
        step("录制：暂停挂太久自己收摊", PauseTooLongProbe);
        step("录制：跟不上帧率也按秒数收，不会超时长", TimeCapProbe);
        step("录制：选区宽高吸到 4 的倍数（H.264 要求）", Snap4Probe);
        step("录制：暂停键要「刚按下」才翻转，按住不会来回切", PauseChordProbe);
        step("录制：真编 MP4，容器和时长都对", Mp4RealProbe);
        step("录制：MP4 从 WPF 界面线程也能编", Mp4UiProbe);
        step("录制：MP4 失败不留下空文件或额外 WebP", Mp4FailureCleanupProbe);
        step("录制：MP4 码率和边长对齐的算法", Mp4MathProbe);
        step("录制：1366×766 这种非 4 倍数尺寸也能编 MP4", Mp4OddSizeProbe);
        step("录制：MP4 旁路文件的后缀还是 .mp4", Mp4SidecarProbe);
        step("录制：MP4 画面方向没反（不是上下颠倒的）", Mp4OrientationProbe);
        step("录制：三种格式的画面方向一致", OrientationAllFormatsProbe);
        step("录制：翻行是原地对调，翻两次回到原样", FlipRowsProbe);
    }

    static void Need(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException(what);
    }

    // ------------------------------------------------------------- 造帧

    const int W = 48;
    const int H = 32;

    /// <summary>几个差得很开的纯色。错位一帧就能立刻看出来。</summary>
    static readonly (byte B, byte G, byte R)[] Colors =
        [(0x30, 0x30, 0xE0), (0x30, 0xC0, 0x30), (0xE0, 0x60, 0x20), (0xF0, 0xF0, 0xF0)];

    static CapturedImage Solid(int i)
    {
        var (b, g, r) = Colors[i % Colors.Length];
        var buf = new byte[W * 4 * H];
        for (var p = 0; p < buf.Length; p += 4)
        {
            buf[p] = b;
            buf[p + 1] = g;
            buf[p + 2] = r;
            buf[p + 3] = 0xFF;
        }
        return new CapturedImage(W, H, buf);
    }

    /// <summary>写 n 帧 PNG 到一个临时目录，返回目录和帧路径。用完记得 Wipe。</summary>
    static (string Dir, List<string> Paths) WriteFrames(int n)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FlashTrans.recprobe." + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var p = Path.Combine(dir, $"f{i:D5}.png");
            Solid(i).SavePng(p);
            paths.Add(p);
        }
        return (dir, paths);
    }

    static void Wipe(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 临时文件，删不掉就算了 */ }
    }

    /// <summary>取一帧正中间那个像素的颜色。纯色帧，取哪个点都一样。</summary>
    static (byte B, byte G, byte R) Center(BitmapSource f)
    {
        var c = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        c.CopyPixels(new System.Windows.Int32Rect(f.PixelWidth / 2, f.PixelHeight / 2, 1, 1), px, 4, 0);
        return (px[0], px[1], px[2]);
    }

    static BitmapFrame[] DecodeGif(string path)
    {
        using var fs = File.OpenRead(path);
        var dec = new GifBitmapDecoder(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return dec.Frames.ToArray();
    }

    // ------------------------------------------------------------- 数学

    static void DelayMathProbe()
    {
        Need(AnimEncoder.DelayCentis(10) == 10, "10 fps 应该是 10 厘秒一帧");
        Need(AnimEncoder.DelayMillis(10) == 100, "10 fps 应该是 100 毫秒一帧");
        Need(AnimEncoder.DelayCentis(30) == 3, "30 fps 约 3 厘秒一帧");
        Need(AnimEncoder.DelayMillis(30) == 33, "30 fps 约 33 毫秒一帧");
        // GIF 的延时不能给 0 或 1：多数浏览器把这两个值当成「按默认速度播」，
        // 结果是录得越快播得越慢，正好反了。夹在 2（=50fps）以上。
        Need(AnimEncoder.DelayCentis(200) == 2, "帧率再高，GIF 延时也不能低于 2 厘秒");
        Need(AnimEncoder.DelayMillis(500) == 10, "WebP 延时不能低于 10 毫秒");
    }

    static void EffectiveFpsProbe()
    {
        // 11 帧铺在 1 秒里 = 10 个间隔 = 10 fps
        Need(Math.Abs(RecordService.Effective(11, 0, 1000, 10) - 10) < 0.01,
            "11 帧 / 1 秒应该算出 10 fps");
        // 目标 10 fps 但实际只抓到 5 帧、跨了 2 秒 —— 必须算出 2，
        // 拿目标值 10 去写延时的话，出来的动图是 5 倍快放
        Need(Math.Abs(RecordService.Effective(5, 0, 2000, 10) - 2) < 0.01,
            "跟不上目标帧率时要算出实测值");
        Need(RecordService.Effective(1, 0, 0, 10) == 10, "只有一帧时用目标帧率");
        Need(RecordService.Effective(2, 100, 100, 10) == 10, "帧间隔为 0 时用目标帧率");
    }

    static void ClampProbe()
    {
        Need(RecordService.ClampFps(0) == RecordService.MinFps, "帧率下限");
        Need(RecordService.ClampFps(999) == RecordService.MaxFps, "帧率上限");
        Need(RecordService.ClampSeconds(0) == RecordService.MinSeconds, "时长下限");
        Need(RecordService.ClampSeconds(99999) == RecordService.MaxSeconds, "时长上限");
    }

    /// <summary>
    /// 拆帧那步碰到不认识的字节要老实返回 null。
    /// 它是按 GIF 的块结构一路往下走的，越界或者猜错块类型就会读出垃圾长度，
    /// 拿那个长度去切数组就是异常。
    /// </summary>
    static void GifJunkProbe()
    {
        Need(AnimEncoder.SplitSingleGif([]) is null, "空字节不该被当成 GIF");
        Need(AnimEncoder.SplitSingleGif([0x89, (byte)'P', (byte)'N', (byte)'G']) is null,
            "PNG 头不该被当成 GIF");
        // 有正确的 GIF 头，但后面被截断了
        Need(AnimEncoder.SplitSingleGif("GIF89a"u8.ToArray()) is null, "截断的 GIF 不该解出帧");

        var (dir, paths) = WriteFrames(1);
        try
        {
            // 一个真文件的字节，但它是 PNG——录制链路上传错文件就是这个样子
            var real = File.ReadAllBytes(paths[0]);
            Need(AnimEncoder.SplitSingleGif(real) is null, "PNG 文件的字节不该解出 GIF 帧");
        }
        finally { Wipe(dir); }
    }

    // ------------------------------------------------------------- GIF

    static void GifRoundTripProbe()
    {
        var (dir, paths) = WriteFrames(5);
        try
        {
            var gif = Path.Combine(dir, "out.gif");
            AnimEncoder.BuildGif(paths, gif, fps: 10);

            Need(new FileInfo(gif).Length > 0, "编出来是个空文件");
            var frames = DecodeGif(gif);
            Need(frames.Length == 5, $"应该有 5 帧，解出来 {frames.Length} 帧");
            Need(frames[0].PixelWidth == W && frames[0].PixelHeight == H,
                $"尺寸不对：{frames[0].PixelWidth}×{frames[0].PixelHeight}");
        }
        finally { Wipe(dir); }
    }

    /// <summary>
    /// 每帧一个差得很开的纯色，解回来逐帧比。
    /// 拼字节流最容易犯的错就是把某一帧的调色板配到另一帧的码流上，
    /// 那样帧数还是对的，但画面全错——只数帧数查不出来。
    /// </summary>
    static void GifFrameOrderProbe()
    {
        var (dir, paths) = WriteFrames(4);
        try
        {
            var gif = Path.Combine(dir, "out.gif");
            AnimEncoder.BuildGif(paths, gif, fps: 10);

            var frames = DecodeGif(gif);
            Need(frames.Length == 4, $"应该有 4 帧，解出来 {frames.Length} 帧");
            for (var i = 0; i < 4; i++)
            {
                var got = Center(frames[i]);
                var want = Colors[i];
                // 容差 8：GIF 是 256 色，量化可能挪一两个色阶
                Need(Math.Abs(got.B - want.B) <= 8
                     && Math.Abs(got.G - want.G) <= 8
                     && Math.Abs(got.R - want.R) <= 8,
                    $"第 {i} 帧颜色不对：要 {want}，得到 {got}");
            }
        }
        finally { Wipe(dir); }
    }

    static void GifLoopProbe()
    {
        var (dir, paths) = WriteFrames(3);
        try
        {
            var gif = Path.Combine(dir, "out.gif");
            AnimEncoder.BuildGif(paths, gif, fps: 10);
            var bytes = File.ReadAllBytes(gif);

            // 没有 NETSCAPE2.0 这个扩展块，GIF 只播一遍就停在最后一帧
            var tag = "NETSCAPE2.0"u8;
            var found = false;
            for (var i = 0; i + tag.Length <= bytes.Length && !found; i++)
                found = bytes.AsSpan(i, tag.Length).SequenceEqual(tag);
            Need(found, "没写 NETSCAPE2.0，动图只会播一遍");
        }
        finally { Wipe(dir); }
    }

    /// <summary>
    /// 每帧的延时。WPF 的 GifBitmapDecoder 读不到帧延时（它的元数据里没这一项），
    /// 所以这里自己按块结构走一遍，把图形控制扩展里的延时挑出来。
    /// 不用「在整个文件里搜那几个字节」：LZW 码流里出现同样的字节是随时可能的，
    /// 那样搜出来的数目对不上，还查不出为什么。
    /// </summary>
    static void GifDelayProbe()
    {
        var (dir, paths) = WriteFrames(3);
        try
        {
            var gif = Path.Combine(dir, "out.gif");
            AnimEncoder.BuildGif(paths, gif, fps: 5);   // 5 fps = 20 厘秒
            var (delays, images) = WalkGif(File.ReadAllBytes(gif));

            Need(images == 3, $"应该有 3 个图像块，走到 {images} 个");
            Need(delays.Count == 3, $"每帧都该有延时，只找到 {delays.Count} 个");
            foreach (var d in delays)
                Need(d == 20, $"5 fps 该是 20 厘秒，得到 {d}");
        }
        finally { Wipe(dir); }
    }

    /// <summary>按 GIF 的块结构走一遍，返回所有帧延时和图像块个数。</summary>
    static (List<int> Delays, int Images) WalkGif(byte[] g)
    {
        var delays = new List<int>();
        var images = 0;

        var packed = g[10];
        var p = 13;
        if ((packed & 0x80) != 0) p += 3 * (1 << ((packed & 0x07) + 1));

        while (p < g.Length && g[p] != 0x3B)
        {
            if (g[p] == 0x21)                       // 扩展块
            {
                var label = g[p + 1];
                if (label == 0xF9) delays.Add(g[p + 4] | (g[p + 5] << 8));
                p += 2;
                p = SkipSubBlocks(g, p);
            }
            else if (g[p] == 0x2C)                  // 图像块
            {
                images++;
                var ip = g[p + 9];
                p += 10;
                if ((ip & 0x80) != 0) p += 3 * (1 << ((ip & 0x07) + 1));
                p++;                                // LZW 最小码长
                p = SkipSubBlocks(g, p);
            }
            else break;                             // 不认识，别猜
        }
        return (delays, images);
    }

    static int SkipSubBlocks(byte[] g, int p)
    {
        while (p < g.Length)
        {
            var len = g[p];
            p++;
            if (len == 0) break;
            p += len;
        }
        return Math.Min(p, g.Length);
    }

    // ------------------------------------------------------------- 工具条

    /// <summary>
    /// 「录制」是工具条上第 N 个按钮，每加一个它就更宽。
    /// PlaceToolbar 会把它夹回画布内，所以不会掉出屏幕，但夹到贴边之后
    /// 它就不再跟着选区右沿走了——选区在屏幕左边时工具条却飘在右边，很难找。
    /// 1366 是还在用的最窄的常见笔记本宽度，超过它就该考虑折行或者收进菜单了。
    ///
    /// 顺便验一下「录制」这个按钮真的在条上：光量宽度的话，
    /// 按钮压根没加上去也一样能过。
    /// </summary>
    static void ToolbarWidthProbe()
    {
        var bar = CaptureOverlay.ToolbarForShot();
        bar.Measure(new System.Windows.Size(
            double.PositiveInfinity, double.PositiveInfinity));
        var w = bar.DesiredSize.Width;

        Need(w > 0, "工具条量出来是 0 宽");
        Need(w <= 1366, $"工具条 {w:F0} DIP，比 1366 还宽，窄屏上摆不下");

        var texts = AllText(bar);
        Need(texts.Contains("录制"), $"工具条上没有「录制」按钮：{texts}");
        Need(texts.Contains("长截图"), "顺手确认原有按钮还在");
    }

    // ------------------------------------------------------------- 浮条

    /// <summary>
    /// 录制浮条。它带 Ellipse、定时器和 WS_EX_* 那套，这些只有真显示出来才走得到
    /// （SourceInitialized 和 Loaded 都得真有窗口句柄）。
    ///
    /// 这一项会在屏幕上闪一下浮条——它自己要贴到选区旁边，摆不到 -4000 去。
    /// </summary>
    static void HudProbe()
    {
        var region = new RECT { Left = 100, Top = 100, Right = 400, Bottom = 300 };
        var hud = new RecordHud(region, maxSeconds: 30, captureAudio: false);
        try
        {
            hud.ShowInTaskbar = false;
            hud.Show();
            hud.UpdateLayout();
            Pump();

            Need(!hud.Stopped, "刚建出来不该是已停止");

            hud.Report(5, TimeSpan.FromSeconds(1.5));
            hud.UpdateLayout();
            var t = AllText(hud);
            Need(t.Contains("5 帧"), $"进度里没有帧数：{t}");
            Need(t.Contains("1.5"), $"进度里没有秒数：{t}");

            hud.ReportEncoding(5);
            hud.UpdateLayout();
            Need(AllText(hud).Contains("编码"), "编码阶段没有改提示");
        }
        finally
        {
            hud.Hide();
            hud.Close();   // 顺带把轮询 Esc 的定时器停掉
        }
    }

    /// <summary>
    /// 「Esc 按着的时候点了录制」这个情况。以前的写法会在头 60ms 内就判定用户要停，
    /// 录出来 0 帧，提示只说「录制没成功」，看不出真正原因。
    ///
    /// 不 Show 也不 Pump：那样定时器不会真的跑起来，喂进去的值就是唯一的输入源，
    /// 不会被机器上此刻 Esc 的真实状态搅进来。
    /// </summary>
    static void EscArmProbe()
    {
        var hud = new RecordHud(new RECT { Left = 0, Top = 0, Right = 100, Bottom = 100 }, 30, captureAudio: false);
        try
        {
            Need(!hud.Stopped, "刚建出来不该是已停止");

            // 一上来就按着 Esc：连着几拍都不该判停
            for (var i = 0; i < 5; i++) hud.WatchEsc(escDown: true);
            Need(!hud.Stopped, "Esc 一直按着（还没松过）不该被判成用户要停");

            hud.WatchEsc(escDown: false);      // 松开了，从这儿起才算
            Need(!hud.Stopped, "松开 Esc 本身不是「停」");

            hud.WatchEsc(escDown: true);
            Need(hud.Stopped, "松开之后再按 Esc 应该停");
        }
        finally { hud.Close(); }
    }

    static string AllText(System.Windows.DependencyObject root)
        => string.Join(" ", Descendants<System.Windows.Controls.TextBlock>(root).Select(t => t.Text));

    static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        if (root is T hit) yield return hit;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
            foreach (var d in Descendants<T>(VisualTreeHelper.GetChild(root, i)))
                yield return d;
    }

    static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    // ------------------------------------------------------------- 真抓屏

    /// <summary>
    /// 真去抓一小块屏幕录 1 秒，然后把录到的帧编成 GIF 再解回来。
    /// 前面那些用造的帧测拼接逻辑，这一项测的是整条链子接没接上：
    /// 抓屏 → 落 PNG → 编码 → 能解开。
    ///
    /// 跑在线程池上（Task.Run 包一层）：这个进程建过 Application，当前线程上装着
    /// DispatcherSynchronizationContext，而自测这会儿没有在跑消息循环。直接
    /// GetResult 的话，RunAsync 里 await 之后的续体会被 Post 回这个不转的 Dispatcher，
    /// 于是双方互等，整个自测挂死。
    /// </summary>
    static void RealRecordProbe()
    {
        var screen = ScreenCapture.VirtualScreen();
        var region = new RECT
        {
            Left = screen.Left + 8,
            Top = screen.Top + 8,
            Right = screen.Left + 8 + 64,
            Bottom = screen.Top + 8 + 48,
        };

        var rec = Task.Run(() => RecordService.RunAsync(region, fps: 4, maxSeconds: 2))
                      .GetAwaiter().GetResult();
        try
        {
            Need(rec.Stopped != RecordStop.Failed, "一帧都没抓到");
            // 4 fps × 2 秒 = 8 帧是上限；机器慢的时候会少抓几帧，但不该少于 3
            Need(rec.Paths.Count is >= 3 and <= 8,
                $"帧数不合理：{rec.Paths.Count}（4 fps 录 2 秒）");
            Need(rec.Paths.All(File.Exists), "有帧文件不在");
            Need(rec.EffectiveFps is >= 1 and <= RecordService.MaxFps,
                $"实测帧率不合理：{rec.EffectiveFps}");

            var gif = Path.Combine(rec.Dir, "real.gif");
            AnimEncoder.BuildGif(rec.Paths, gif, (int)Math.Round(rec.EffectiveFps));
            var frames = DecodeGif(gif);
            Need(frames.Length == rec.Paths.Count,
                $"编进去 {rec.Paths.Count} 帧，解出来 {frames.Length} 帧");
            Need(frames[0].PixelWidth == 64 && frames[0].PixelHeight == 48,
                $"尺寸不对：{frames[0].PixelWidth}×{frames[0].PixelHeight}");
        }
        finally
        {
            rec.Cleanup();
        }
        Need(!Directory.Exists(rec.Dir), "临时目录没被清掉");
    }

    // ------------------------------------------------------------- WebP

    /// <summary>
    /// 真编一张动图 WebP，然后按 RIFF 容器结构验它是不是「动」的。
    ///
    /// 只验「文件生成了、能打开」不够——一张静态 WebP 也能过那种断言。
    /// 动图 WebP 的标志是 VP8X 里的 animation 位、一个 ANIM 块（带循环次数）
    /// 和每帧一个 ANMF 块（带这一帧的毫秒延时），逐个查出来才算数。
    ///
    /// img2webp.exe 不在时跳过：它不是编译产物，机器上可能压根没放。
    /// </summary>
    static void WebpRealProbe()
    {
        if (!AnimEncoder.WebpAvailable) return;

        var (dir, paths) = WriteFrames(4);
        try
        {
            var res = Task.Run(() => AnimEncoder.SaveAsync(
                    paths, Path.Combine(dir, "out"), fps: 10, RecordFormat.Webp))
                .GetAwaiter().GetResult();

            Need(!res.FellBack, "有 img2webp 却退回了 GIF");
            Need(res.Format == RecordFormat.Webp, $"格式不是 WebP：{res.Format}");
            Need(res.Path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase),
                $"后缀不对：{res.Path}");
            Need(res.Bytes > 0 && res.Bytes == new FileInfo(res.Path).Length,
                "报告的大小跟文件实际大小不一致");

            var (animBit, loop, durations) = WalkWebp(File.ReadAllBytes(res.Path));
            Need(animBit, "VP8X 里没置 animation 位，这是张静态 WebP");
            Need(loop == 0, $"循环次数该是 0（无限），得到 {loop}");
            Need(durations.Count == 4, $"该有 4 个 ANMF 帧，得到 {durations.Count}");
            foreach (var d in durations)
                Need(d == 100, $"10 fps 该是 100 毫秒一帧，得到 {d}");
        }
        finally { Wipe(dir); }
    }

    /// <summary>
    /// 走一遍 WebP 的 RIFF 容器，挑出动图相关的那三样：
    /// VP8X 的 animation 位、ANIM 的循环次数、每个 ANMF 的帧延时。
    /// </summary>
    static (bool AnimBit, int Loop, List<int> Durations) WalkWebp(byte[] d)
    {
        Need(d.Length > 12, "WebP 文件太短");
        Need(d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F', "不是 RIFF");
        Need(d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P', "不是 WEBP");

        var animBit = false;
        var loop = -1;
        var durations = new List<int>();

        var p = 12;
        while (p + 8 <= d.Length)
        {
            var cc = System.Text.Encoding.ASCII.GetString(d, p, 4);
            var size = BitConverter.ToInt32(d, p + 4);
            if (size < 0 || p + 8 + size > d.Length) break;
            var body = p + 8;

            switch (cc)
            {
                // VP8X 的第一个字节是标志位，bit1 = animation
                case "VP8X" when size >= 1:
                    animBit = (d[body] & 0x02) != 0;
                    break;
                // ANIM：4 字节背景色 + 2 字节循环次数
                case "ANIM" when size >= 6:
                    loop = d[body + 4] | (d[body + 5] << 8);
                    break;
                // ANMF：帧头 16 字节，第 12..14 是 24 位小端的毫秒延时
                case "ANMF" when size >= 16:
                    durations.Add(d[body + 12] | (d[body + 13] << 8) | (d[body + 14] << 16));
                    break;
            }
            p = body + size + (size & 1);   // 块要按偶数字节对齐
        }
        return (animBit, loop, durations);
    }

    /// <summary>
    /// 把 img2webp 挪走，确认这时候选 WebP 会老实退回 GIF 而不是报错。
    /// 用户拿绿色版只拷了个 exe 走就是这个情况，不该让人白录一遍。
    /// 挪走的文件在 finally 里放回去。
    /// </summary>
    static void WebpFallbackProbe()
    {
        var tool = AnimEncoder.FindImg2Webp();
        if (tool is null) return;                 // 本来就没有，这项没意义
        var aside = tool + ".probe-aside";

        var (dir, paths) = WriteFrames(2);
        try
        {
            File.Move(tool, aside);
            Need(!AnimEncoder.WebpAvailable, "挪走之后还认为 WebP 可用");

            var res = Task.Run(() => AnimEncoder.SaveAsync(
                    paths, Path.Combine(dir, "fb"), fps: 10, RecordFormat.Webp))
                .GetAwaiter().GetResult();

            Need(res.FellBack, "没有 img2webp 时该标记「退回了 GIF」");
            Need(res.Format == RecordFormat.Gif, $"该退回 GIF，得到 {res.Format}");
            Need(res.Path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase),
                $"后缀该是 .gif：{res.Path}");
            Need(DecodeGif(res.Path).Length == 2, "退回的 GIF 帧数不对");
        }
        finally
        {
            if (File.Exists(aside)) File.Move(aside, tool, overwrite: true);
            Wipe(dir);
        }
        Need(AnimEncoder.WebpAvailable, "探针没把 img2webp 放回去");
    }

    /// <summary>
    /// 拿真实屏幕内容两种格式各编一遍比大小。
    ///
    /// 这一项不光是断言，也是把数字打出来看——「WebP 比 GIF 小」这话得有个量级。
    /// 用真实屏幕而不是造的纯色帧：纯色帧 LZW 能压到几百字节，两边都小得没意义，
    /// 比出来的比例跟实际录屏差得远。
    /// </summary>
    static void SizeCompareProbe()
    {
        if (!AnimEncoder.WebpAvailable) return;

        var screen = ScreenCapture.VirtualScreen();
        var region = new RECT
        {
            Left = screen.Left + 8,
            Top = screen.Top + 8,
            Right = Math.Min(screen.Right, screen.Left + 8 + 640),
            Bottom = Math.Min(screen.Bottom, screen.Top + 8 + 480),
        };

        var rec = Task.Run(() => RecordService.RunAsync(region, fps: 5, maxSeconds: 2))
                      .GetAwaiter().GetResult();
        try
        {
            Need(rec.Paths.Count >= 3, $"抓到的帧太少：{rec.Paths.Count}");
            var fps = Math.Max(1, (int)Math.Round(rec.EffectiveFps));

            var webp = Task.Run(() => AnimEncoder.SaveAsync(
                    rec.Paths, Path.Combine(rec.Dir, "cmp"), fps, RecordFormat.Webp))
                .GetAwaiter().GetResult();
            var gif = Task.Run(() => AnimEncoder.SaveAsync(
                    rec.Paths, Path.Combine(rec.Dir, "cmp"), fps, RecordFormat.Gif))
                .GetAwaiter().GetResult();

            var line = $"       {rec.Paths.Count} 帧 "
                + $"{region.Right - region.Left}×{region.Bottom - region.Top}："
                + $"GIF {gif.Bytes / 1024.0:0} KB → WebP {webp.Bytes / 1024.0:0} KB"
                + $"（小 {gif.Bytes / (double)webp.Bytes:0.#} 倍）";

            if (Mp4Encoder.Available)
            {
                var mp4 = Task.Run(() => AnimEncoder.SaveAsync(
                        rec.Paths, Path.Combine(rec.Dir, "cmp"), fps, RecordFormat.Mp4))
                    .GetAwaiter().GetResult();
                if (mp4.Format == RecordFormat.Mp4)
                {
                    line += $" → MP4 {mp4.Bytes / 1024.0:0} KB"
                        + $"（小 {gif.Bytes / (double)mp4.Bytes:0.#} 倍）";
                    Need(mp4.Bytes < gif.Bytes,
                        $"MP4 反而比 GIF 大：{mp4.Bytes} vs {gif.Bytes}");
                }
            }
            Console.WriteLine(line);

            Need(webp.Bytes < gif.Bytes,
                $"WebP 反而更大：{webp.Bytes} vs GIF {gif.Bytes}");
        }
        finally { rec.Cleanup(); }
    }

    // ------------------------------------------------------------- 暂停

    /// <summary>屏幕左上角一小块，够快就行。</summary>
    static RECT SmallRegion(int w = 64, int h = 48)
    {
        var s = ScreenCapture.VirtualScreen();
        return new RECT
        {
            Left = s.Left + 8, Top = s.Top + 8,
            Right = s.Left + 8 + w, Bottom = s.Top + 8 + h,
        };
    }

    static CapturedImage FakeCapture(RECT region)
    {
        var width = Math.Max(2, region.Right - region.Left);
        var height = Math.Max(2, region.Bottom - region.Top);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x40;
            pixels[i + 1] = 0x80;
            pixels[i + 2] = 0xC0;
            pixels[i + 3] = 0xFF;
        }
        return new CapturedImage(width, height, pixels);
    }

    /// <summary>
    /// 暂停期间一帧都不该抓，恢复之后还能接着抓。
    ///
    /// 直接数帧：先让它录一会儿，按下暂停，记住那一刻的帧数，等半秒再看还是不是
    /// 这个数——涨了就说明暂停没拦住抓帧，出来的动图里会多一段静止画面。
    /// </summary>
    static void PauseProbe()
    {
        var paused = false;
        var frames = 0;
        var rec = Task.Run(async () =>
        {
            var run = RecordService.RunAsync(SmallRegion(), fps: 10, maxSeconds: 10,
                onProgress: (n, _) => frames = n,
                paused: () => Volatile.Read(ref paused),
                capture: FakeCapture);

            // 录够几帧再按暂停
            while (Volatile.Read(ref frames) < 3) await Task.Delay(20);

            Volatile.Write(ref paused, true);
            await Task.Delay(150);                       // 让它把手上那一帧走完
            var atPause = Volatile.Read(ref frames);
            await Task.Delay(500);                       // 暂停期间：这半秒不该有新帧
            var afterHold = Volatile.Read(ref frames);

            Volatile.Write(ref paused, false);
            await Task.Delay(400);                       // 恢复之后该继续涨
            var afterResume = Volatile.Read(ref frames);

            return (await run, atPause, afterHold, afterResume);
        }).GetAwaiter().GetResult();

        var (r, atPause, afterHold, afterResume) = rec;
        try
        {
            Need(afterHold == atPause,
                $"暂停期间还在抓帧：{atPause} → {afterHold}");
            Need(afterResume > afterHold,
                $"恢复之后没接着录：{afterHold} → {afterResume}");
            Need(r.Pauses == 1, $"该记录 1 次暂停，得到 {r.Pauses}");
            Need(r.PausedFor.TotalMilliseconds >= 400,
                $"暂停时长记少了：{r.PausedFor.TotalMilliseconds:0} 毫秒");
            Need(r.Paths.Count == r.Paths.Distinct().Count(), "帧文件名有重复");
            Need(r.Paths.All(File.Exists), "有帧文件不在");
        }
        finally { r.Cleanup(); }
    }

    /// <summary>
    /// 暂停掉的时间不进 Elapsed，也不吃时长预算。
    ///
    /// 这是「回放连续」的根据：Elapsed 是拿来算实测帧率、再换成每帧延时的。
    /// 要是把暂停的 5 秒算进去，帧率会被算低好几倍，出来的动图慢得像卡住。
    /// </summary>
    static void PauseClockProbe()
    {
        var paused = false;
        var r = Task.Run(async () =>
        {
            var run = RecordService.RunAsync(SmallRegion(), fps: 10, maxSeconds: 3,
                paused: () => Volatile.Read(ref paused),
                capture: FakeCapture);
            await Task.Delay(300);
            Volatile.Write(ref paused, true);
            await Task.Delay(700);
            Volatile.Write(ref paused, false);
            return await run;
        }).GetAwaiter().GetResult();

        try
        {
            // 墙上时间至少是 3 秒预算 + 0.7 秒暂停；Elapsed 该只有预算那部分。
            Need(r.Elapsed.TotalSeconds <= 3.5,
                $"暂停的时间被算进 Elapsed 了：{r.Elapsed.TotalSeconds:0.0}s（预算 3s）");
            Need(r.PausedFor.TotalMilliseconds >= 600,
                $"暂停时长记少了：{r.PausedFor.TotalMilliseconds:0} 毫秒");
            // 预算没被暂停吃掉：3 秒 10 fps，就算慢也该比「只录了 0.3 秒」多得多
            Need(r.Paths.Count >= 8,
                $"暂停吃掉了时长预算，只录到 {r.Paths.Count} 帧（3 秒 10 fps）");
            Need(r.EffectiveFps >= 4,
                $"实测帧率被暂停拖低了：{r.EffectiveFps:0.#}");
        }
        finally { r.Cleanup(); }
    }

    /// <summary>
    /// 按了暂停就走开：到闸就自己收，已经录到的帧照样交出来。
    /// 用 maxPausedMs 把 10 分钟那道闸调到 300 毫秒，不然这一项得跑十分钟。
    /// </summary>
    static void PauseTooLongProbe()
    {
        var frames = 0;
        var r = Task.Run(async () =>
        {
            var run = RecordService.RunAsync(SmallRegion(), fps: 10, maxSeconds: 60,
                onProgress: (n, _) => frames = n,
                paused: () => Volatile.Read(ref frames) >= 2,   // 录到 2 帧就一直暂停着
                maxPausedMs: 300,
                capture: FakeCapture);
            return await run;
        }).GetAwaiter().GetResult();

        try
        {
            Need(r.Stopped == RecordStop.PausedTooLong,
                $"该因为暂停太久收摊，得到 {r.Stopped}");
            Need(r.Paths.Count >= 2, $"已经录到的帧该交出来，得到 {r.Paths.Count}");
            Need(r.Paths.All(File.Exists), "有帧文件不在");
            Need(r.PausedFor.TotalMilliseconds >= 250,
                $"暂停时长没记上：{r.PausedFor.TotalMilliseconds:0} 毫秒");
        }
        finally { r.Cleanup(); }
    }

    /// <summary>
    /// 时长上限是按秒算的，不是「录满 fps × 秒数 帧就收」。
    ///
    /// 要 30 fps 而机器只跟得上十几帧的时候，光数帧的话「最长 2 秒」会变成
    /// 录满 60 帧、实际过了四五秒。这里要一块大区域 + 高帧率去逼出「跟不上」，
    /// 然后验墙上时间没超出上限太多。
    /// </summary>
    static void TimeCapProbe()
    {
        // 要真的跟不上才测得到这条：拿整个虚拟屏 + 30 fps。一帧全屏 BitBlt 加 PNG
        // 编码远不止 33 毫秒，帧数一定凑不满 fps × 秒数，这时候只能靠时间收。
        var s = ScreenCapture.VirtualScreen();
        var r = Task.Run(() => RecordService.RunAsync(s, fps: 30, maxSeconds: 2))
                    .GetAwaiter().GetResult();
        try
        {
            Need(r.Stopped != RecordStop.Failed, "一帧都没抓到");
            var full = 30 * 2;
            Need(r.Paths.Count < full,
                $"全屏 30 fps 居然录满了 {full} 帧，这一项没测到时长那条规则"
                + "（换更大的区域或更高的帧率）");
            Need(r.Elapsed.TotalSeconds <= 2.8,
                $"超了时长上限：{r.Elapsed.TotalSeconds:0.00}s（上限 2s，"
                + $"抓到 {r.Paths.Count} 帧 / 上限 {full} 帧）");
            Console.WriteLine($"       全屏 {s.Right - s.Left}×{s.Bottom - s.Top} 要 {full} 帧，"
                              + $"实际 {r.Paths.Count} 帧 {r.Elapsed.TotalSeconds:0.00}s，"
                              + $"实测 {r.EffectiveFps:0.#} fps");
        }
        finally { r.Cleanup(); }
    }

    /// <summary>
    /// 宽高吸到 4 的倍数，左上角不动。
    /// 系统 H.264 编码器差 2 就抛 0x80004005，不是只要偶数就行。
    /// </summary>
    static void Snap4Probe()
    {
        var odd = new RECT { Left = 11, Top = 7, Right = 11 + 101, Bottom = 7 + 55 };
        var s = RecordService.Snap4(odd);
        Need(s.Left == 11 && s.Top == 7, $"左上角动了：{s.Left},{s.Top}");
        Need(s.Right - s.Left == 100, $"宽该吸到 100，得到 {s.Right - s.Left}");
        Need(s.Bottom - s.Top == 52, $"高该吸到 52，得到 {s.Bottom - s.Top}");

        var even = new RECT { Left = 0, Top = 0, Right = 64, Bottom = 48 };
        var t = RecordService.Snap4(even);
        Need(t.Right - t.Left == 64 && t.Bottom - t.Top == 48, "本来是 4 的倍数的被改了");

        // 1366×768 是用户那台的屏幕宽度：偶数，但不是 4 的倍数。
        // 这一格就是「选了 MP4 直接报错」的原样，别再放它过去。
        var screen = new RECT { Left = 0, Top = 0, Right = 1366, Bottom = 768 };
        var f = RecordService.Snap4(screen);
        Need(f.Right - f.Left == 1364, $"1366 该吸到 1364，得到 {f.Right - f.Left}");
        Need(f.Bottom - f.Top == 768, $"768 本来就对，被改成了 {f.Bottom - f.Top}");
        Need((f.Right - f.Left) % 4 == 0 && (f.Bottom - f.Top) % 4 == 0, "吸完还不是 4 的倍数");

        // 空选区不该变成负数
        var empty = new RECT { Left = 5, Top = 5, Right = 5, Bottom = 5 };
        var e = RecordService.Snap4(empty);
        Need(e.Right - e.Left == 0 && e.Bottom - e.Top == 0, "空选区被算出了负宽高");
    }

    /// <summary>
    /// 暂停键的边沿判定：按住不放只翻一次。
    /// 轮询是 60 毫秒一拍，按住 200 毫秒就是三四拍，
    /// 判定写成「按着就翻」的话一次按键会来回切好几次，看起来就是「暂停键失灵」。
    /// </summary>
    static void PauseChordProbe()
    {
        var hud = new RecordHud(SmallRegion(), 30, captureAudio: false);
        try
        {
            hud.Show();
            Pump();
            Need(!hud.Paused, "刚开始就是暂停状态");

            hud.WatchPauseChord(true);     // 按下
            Need(hud.Paused, "按下没进暂停");
            hud.WatchPauseChord(true);     // 按住
            hud.WatchPauseChord(true);
            Need(hud.Paused, "按住把暂停又翻回去了");

            hud.WatchPauseChord(false);    // 松开：不该有动作
            Need(hud.Paused, "松开就取消暂停了");

            hud.WatchPauseChord(true);     // 再按一次：恢复
            Need(!hud.Paused, "再按一次没恢复");

            // 编码阶段按了不算——那时候已经没帧可录
            hud.ReportEncoding(5);
            hud.WatchPauseChord(false);
            hud.WatchPauseChord(true);
            Need(!hud.Paused, "编码阶段还能被按成暂停");
        }
        finally { hud.Close(); Pump(); }
    }

    // ------------------------------------------------------------- MP4

    /// <summary>
    /// 真编一个 MP4，然后按 ISO BMFF 的盒子结构验。
    ///
    /// 跟 WebP 那项一个思路：只验「文件存在、不是 0 字节」太松——转码半路失败也可能
    /// 留下个有 ftyp 没 mdat 的残文件，播起来就是 0 秒。所以这里查四件事：
    /// ftyp 在最前、moov 和 mdat 都在、时长跟帧数对得上。
    ///
    /// 系统没有 H.264 编码器时跳过（精简版系统会这样）。
    /// </summary>
    static void Mp4RealProbe()
    {
        if (!Mp4Encoder.Available) return;

        var (dir, paths) = WriteFrames(10);
        try
        {
            var res = Task.Run(() => AnimEncoder.SaveAsync(
                    paths, Path.Combine(dir, "out"), fps: 10, RecordFormat.Mp4))
                .GetAwaiter().GetResult();

            Need(res.Format == RecordFormat.Mp4,
                $"格式不是 MP4：{res.Format}（{res.FellBackWhy}）");
            Need(!res.FellBack, $"退档了：{res.FellBackWhy}");
            Need(res.Path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase),
                $"后缀不对：{res.Path}");
            Need(res.Bytes > 0 && res.Bytes == new FileInfo(res.Path).Length,
                "报告的大小跟文件实际大小不一致");

            var (boxes, seconds) = WalkMp4(File.ReadAllBytes(res.Path));
            Need(boxes.Count > 0 && boxes[0] == "ftyp",
                $"第一个盒子该是 ftyp，得到 {(boxes.Count > 0 ? boxes[0] : "空")}");
            Need(boxes.Contains("moov"), "没有 moov 盒子，播放器读不出这是什么");
            Need(boxes.Contains("mdat"), "没有 mdat 盒子，里面一帧画面都没有");
            // 10 帧 10 fps = 1 秒。转码器对首尾帧的处理有出入，给宽一点。
            Need(seconds is >= 0.5 and <= 2.0,
                $"时长不对：{seconds:0.00} 秒（10 帧 10 fps 该是 1 秒左右）");
            Console.WriteLine($"       MP4 {res.Bytes / 1024.0:0} KB，时长 {seconds:0.00}s，"
                              + $"盒子：{string.Join(" ", boxes.Take(6))}");
        }
        finally { Wipe(dir); }
    }

    static void Mp4UiProbe()
    {
        if (!Mp4Encoder.Available) return;

        var (dir, paths) = WriteFrames(10);
        try
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            var task = AnimEncoder.SaveAsync(
                paths, Path.Combine(dir, "ui-out"), fps: 10, RecordFormat.Mp4);
            var frame = new System.Windows.Threading.DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            var res = task.GetAwaiter().GetResult();
            Need(res.Format == RecordFormat.Mp4 && !res.FellBack,
                $"UI 线程转码退档：{res.Format}（{res.FellBackWhy}）");
            Need(res.Bytes > 0 && new FileInfo(res.Path).Length == res.Bytes,
                "UI 线程转码输出为空或大小不一致");
            var (boxes, seconds) = WalkMp4(File.ReadAllBytes(res.Path));
            Need(boxes.Contains("moov") && boxes.Contains("mdat"),
                "UI 线程转码缺少 MP4 数据盒子");
            Need(seconds is >= 0.5 and <= 2.0,
                $"UI 线程转码时长不对：{seconds:0.00} 秒");
        }
        finally { Wipe(dir); }
    }

    /// <summary>
    /// 边长是「偶数但不是 4 的倍数」时也得能编出来。
    ///
    /// 这是 1.7.0「录制视频直接错误」的真凶。上面那两个 MP4 探针用的是 48×32，
    /// 正好都是 4 的倍数，所以一路绿灯，而用户 1366 宽的屏幕一录就抛
    /// COMException 0x80004005：CanTranscode 那步还是 true，报错要等到真开始转码。
    /// `--mp4lab` 按边长扫出来的：636 成 638 败 640 成 642 败，宽高两边一样。
    ///
    /// 所以这里故意造 1366×766 这种尺寸，逼着 SaveAsync 自己把边长吸下去。
    /// 帧给得少一点：这个尺寸一帧就是 4MB，编太多这项会拖慢整个自测。
    /// </summary>
    static void Mp4OddSizeProbe()
    {
        if (!Mp4Encoder.Available) return;

        // 宽高都是偶数、都不是 4 的倍数；1366 就是用户那台的屏幕宽度
        var dir = Path.Combine(Path.GetTempPath(), "FlashTrans.mp4odd." + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            const int w = 1366, h = 766;
            var paths = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                var (b, g, r) = Colors[i % Colors.Length];
                var buf = new byte[w * 4 * h];
                for (var p = 0; p < buf.Length; p += 4)
                {
                    buf[p] = b; buf[p + 1] = g; buf[p + 2] = r; buf[p + 3] = 0xFF;
                }
                var f = Path.Combine(dir, $"o{i:D5}.png");
                new CapturedImage(w, h, buf).SavePng(f);
                paths.Add(f);
            }

            var res = Task.Run(() => Mp4Encoder.SaveAsync(paths, Path.Combine(dir, "odd.mp4"), 10))
                          .GetAwaiter().GetResult();
            Need(res.Bytes > 0, "1366×766 编出来是 0 字节");
            var (boxes, _) = WalkMp4(File.ReadAllBytes(res.Path));
            Need(boxes.Contains("moov") && boxes.Contains("mdat"),
                "1366×766 编出来缺 MP4 数据盒子");
        }
        finally { Wipe(dir); }
    }

    /// <summary>
    /// 旁路文件跟最终文件不同名，后缀还是 .mp4。
    ///
    /// 后缀这条是防御性的：Media Foundation 挑写出器时参考扩展名。
    /// （1.7.0 那版「MP4 一选就报错」的真因不在这儿，是边长不是 4 的倍数，
    /// 见 Mp4MathProbe 和 Mp4OddSizeProbe。）
    /// </summary>
    static void Mp4SidecarProbe()
    {
        var side = Mp4Encoder.SidecarPath(@"C:\x\闪译录制 2026-09-01 190529.mp4");
        Need(side.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase),
            $"旁路文件后缀不是 .mp4，转码会抛 0x80004005：{side}");
        Need(!side.EndsWith(".mp4.part", StringComparison.OrdinalIgnoreCase),
            $"旁路文件又变回 .mp4.part 了：{side}");
        Need(side != @"C:\x\闪译录制 2026-09-01 190529.mp4",
            "旁路文件跟最终文件同名，转码会直接写进用户看到的那个文件");
        Need(Path.GetFileName(side).Contains(".part", StringComparison.Ordinal),
            $"旁路文件名里看不出是临时文件：{side}");
    }

    /// <summary>
    /// 编出来的 MP4 不能是上下颠倒的。
    ///
    /// 1.7.1 之前就是颠倒的，而上面那些 MP4 探针一项都没红——帧数、时长、
    /// ftyp/moov/mdat 全对，只有每帧的行序反了。原因是 Media Foundation 把
    /// 未压缩 RGB 当成自底向上（bottom-up），而我们喂的是自顶向下的行。
    ///
    /// 造一帧四个角亮度都不同的图，编完解回来比四个角：只验上下会漏掉左右镜像。
    /// 用亮度不用颜色——H.264 是 4:2:0，色度抽样过纯色也会偏，亮度不会。
    /// </summary>
    static void Mp4OrientationProbe()
    {
        if (!Mp4Encoder.Available) return;

        var (dir, paths) = WriteCornerFrames(4);
        try
        {
            var mp4 = Path.Combine(dir, "orient.mp4");
            Task.Run(() => Mp4Encoder.SaveAsync(paths, mp4, 10)).GetAwaiter().GetResult();

            // 先验源帧本身是对的，不然下面红了分不清是谁的锅
            NeedCorners(FlipCheck.Load(paths[0]), "源 PNG");
            NeedCorners(Task.Run(() => FlipCheck.FirstMp4FrameAsync(mp4)).GetAwaiter().GetResult(),
                "MP4 解回来的第一帧");
        }
        finally { Wipe(dir); }
    }

    /// <summary>
    /// 三种格式方向要一致。GIF 和 WebP 走 AnimEncoder / img2webp，不碰
    /// MediaStreamSource，本来就是对的；这一项盯的是「以后谁改了某一条路
    /// 的行序，另外两条还是对的」这种不一致。
    /// </summary>
    static void OrientationAllFormatsProbe()
    {
        var (dir, paths) = WriteCornerFrames(4);
        try
        {
            foreach (var fmt in (RecordFormat[])[RecordFormat.Gif, RecordFormat.Webp, RecordFormat.Mp4])
            {
                if (fmt == RecordFormat.Mp4 && !Mp4Encoder.Available) continue;
                var res = Task.Run(() => AnimEncoder.SaveAsync(
                    paths, Path.Combine(dir, "orient_" + fmt), 10, fmt)).GetAwaiter().GetResult();

                var img = res.Format == RecordFormat.Mp4
                    ? Task.Run(() => FlipCheck.FirstMp4FrameAsync(res.Path)).GetAwaiter().GetResult()
                    : FlipCheck.Load(res.Path);
                NeedCorners(img, $"{res.Format} 的第一帧");
            }
        }
        finally { Wipe(dir); }
    }

    /// <summary>四个角的亮度要还是「左上最亮、右下最暗」。</summary>
    static void NeedCorners(System.Windows.Media.Imaging.BitmapSource img, string what)
    {
        var (tl, tr, bl, br) = FlipCheck.Quadrants(img);
        var got = $"{what} 四角亮度 {tl:0}/{tr:0}/{bl:0}/{br:0}（要 240/160/80/0）";
        // 容差 30：GIF 只有 256 色要量化，H.264 有损，边界还会糊
        Need(Math.Abs(tl - 240) <= 30 && Math.Abs(tr - 160) <= 30
             && Math.Abs(bl - 80) <= 30 && Math.Abs(br - 30) <= 60,
            bl > tl && br > tr ? got + "：上下颠倒了" : got);
    }

    /// <summary>写 n 帧「四角亮度不同」的 PNG。</summary>
    static (string Dir, List<string> Paths) WriteCornerFrames(int n)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FlashTrans.orient." + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        for (var i = 0; i < n; i++)
        {
            // 240×160：都是 4 的倍数，这一项要盯的是方向不是对齐
            var p = Path.Combine(dir, $"c{i:D5}.png");
            FlipCheck.Corners(240, 160).SavePng(p);
            paths.Add(p);
        }
        return (dir, paths);
    }

    static void FlipRowsProbe()
    {
        // 3 行 × 每行 2 像素，行内容分别是全 1、全 2、全 3
        const int stride = 8, h = 3;
        var buf = new byte[stride * h];
        for (var y = 0; y < h; y++)
            for (var i = 0; i < stride; i++) buf[y * stride + i] = (byte)(y + 1);

        Mp4Encoder.FlipRows(buf, stride, h);
        Need(buf[0] == 3 && buf[stride] == 2 && buf[2 * stride] == 1,
            $"翻完的行序不对：{buf[0]}/{buf[stride]}/{buf[2 * stride]}");

        Mp4Encoder.FlipRows(buf, stride, h);
        Need(buf[0] == 1 && buf[stride] == 2 && buf[2 * stride] == 3, "翻两次没回到原样");

        // 奇数行时中间那行留在原地，别把它也搬了
        Need(buf.Skip(stride).Take(stride).All(b => b == 2), "中间那行被改坏了");
    }

    static void Mp4FailureCleanupProbe()
    {
        if (!Mp4Encoder.Available) return;

        var (dir, paths) = WriteFrames(1);
        paths.Add(Path.Combine(dir, "missing-frame.png"));
        var outNoExt = Path.Combine(dir, "failed-output");
        try
        {
            try
            {
                Task.Run(() => AnimEncoder.SaveAsync(
                    paths, outNoExt, fps: 10, RecordFormat.Mp4))
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException("无效帧没有让 MP4 编码失败");
            }
            catch (InvalidOperationException) { }

            Need(!File.Exists(outNoExt + ".mp4"), "MP4 失败后留下了最终文件");
            Need(!File.Exists(Mp4Encoder.SidecarPath(outNoExt + ".mp4")),
                "MP4 失败后留下了临时文件");
            Need(!File.Exists(outNoExt + ".webp"), "MP4 失败后偷偷生成了 WebP");
        }
        finally { Wipe(dir); }
    }

    /// <summary>
    /// 走一遍 MP4 顶层盒子，并从 moov/mvhd 里读时长。
    ///
    /// mvhd 的布局：版本(1) 标志(3) 创建(4) 修改(4) 时间刻度(4) 时长(4)，
    /// 版本 1 的话创建/修改/时长都是 8 字节。盒子长度和字段都是大端。
    /// </summary>
    static (List<string> Boxes, double Seconds) WalkMp4(byte[] b)
    {
        var boxes = new List<string>();
        double seconds = 0;
        var p = 0;
        while (p + 8 <= b.Length)
        {
            var size = (long)Be32(b, p);
            var type = System.Text.Encoding.ASCII.GetString(b, p + 4, 4);
            var head = 8;
            if (size == 1)
            {
                // 64 位长度，紧跟在类型后面
                if (p + 16 > b.Length) break;
                size = (long)Be32(b, p + 8) << 32 | Be32(b, p + 12);
                head = 16;
            }
            else if (size == 0) size = b.Length - p;   // 到文件尾
            if (size < head || p + size > b.Length) break;

            boxes.Add(type);
            if (type == "moov") seconds = MvhdSeconds(b, p + head, p + (int)size);
            p += (int)size;
        }
        return (boxes, seconds);
    }

    /// <summary>在 moov 的子盒子里找 mvhd，把时长算成秒。</summary>
    static double MvhdSeconds(byte[] b, int from, int to)
    {
        var p = from;
        while (p + 8 <= to)
        {
            var size = (int)Be32(b, p);
            var type = System.Text.Encoding.ASCII.GetString(b, p + 4, 4);
            if (size < 8 || p + size > to) break;
            if (type == "mvhd")
            {
                var v = b[p + 8];
                var q = p + 12;                       // 跳过版本和标志
                q += v == 1 ? 16 : 8;                 // 创建 + 修改
                var scale = Be32(b, q);
                var dur = v == 1 ? (double)((long)Be32(b, q + 4) << 32 | Be32(b, q + 8))
                                 : Be32(b, q + 4);
                return scale == 0 ? 0 : dur / scale;
            }
            p += size;
        }
        return 0;
    }

    static uint Be32(byte[] b, int i)
        => (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);

    /// <summary>码率和边长对齐这两个纯算法。</summary>
    static void Mp4MathProbe()
    {
        // 吸到 4 的倍数，不是只到偶数：638、1366 这种差 2 的会让编码器抛 0x80004005
        Need(Mp4Encoder.Align4(101) == 100, "101 该吸到 100");
        Need(Mp4Encoder.Align4(100) == 100, "100 本来就对，被改了");
        Need(Mp4Encoder.Align4(102) == 100, "102 该吸到 100");
        Need(Mp4Encoder.Align4(103) == 100, "103 该吸到 100");
        Need(Mp4Encoder.Align4(638) == 636, "638 该吸到 636");
        Need(Mp4Encoder.Align4(1366) == 1364, "1366 该吸到 1364");
        Need(Mp4Encoder.Align4(3) == 0, "3 该吸成 0");
        Need(Mp4Encoder.Align4(0) == 0, "0 被改了");
        foreach (var v in (int[])[2, 66, 478, 1078, 1918, 2559])
            Need(Mp4Encoder.Align4(v) % 4 == 0, $"{v} 吸完不是 4 的倍数");

        // 大区域高帧率也不该超上限，小区域也不该低到糊成一片
        var big = Mp4Encoder.Bitrate(3840, 2160, 30);
        var small = Mp4Encoder.Bitrate(64, 48, 2);
        Need(big <= 40_000_000, $"码率超了上限：{big}");
        Need(small >= 800_000, $"码率低于下限：{small}");
        Need(Mp4Encoder.Bitrate(1920, 1080, 10) > Mp4Encoder.Bitrate(640, 480, 10),
            "大区域的码率该比小区域高");
        // 比帧率要挑个大到不会撞下限的尺寸：640×480 无论几帧都低于 800 kbps，
        // 两边都被夹到下限，比出来一样大——那是下限在起作用，不是算法错了。
        Need(Mp4Encoder.Bitrate(1920, 1080, 10) > Mp4Encoder.Bitrate(1920, 1080, 5),
            "帧率高的码率该更高");
        Need(Mp4Encoder.Bitrate(640, 480, 20) == Mp4Encoder.Bitrate(640, 480, 5),
            "小尺寸该都撞在下限上");
    }
}
