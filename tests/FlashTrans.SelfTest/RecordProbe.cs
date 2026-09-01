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
        step("录制：帧率和时长的夹取范围", ClampProbe);
        step("录制：浮条能构造，进度和编码提示都会更新", HudProbe);
        step("录制：工具条多了「录制」还塞得进常见屏宽", ToolbarWidthProbe);
        step("录制：Esc 要松开过一次才算停，不会开录就被判停", EscArmProbe);
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
        var hud = new RecordHud(region, maxSeconds: 30);
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
        var hud = new RecordHud(new RECT { Left = 0, Top = 0, Right = 100, Bottom = 100 }, 30);
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
}
