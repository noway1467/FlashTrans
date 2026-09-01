using System.IO;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.SelfTest;

/// <summary>
/// 量三种格式的体积。README 上那张对照表就是这儿出的数，改了编码参数可以重跑一遍。
///
/// `--sizelab` 录真实屏幕，顺带按几个绝对码率各编一遍 MP4——这是用来确认
/// 「码率这个旋钮到底有没有接上」的：接上了体积该跟着变，四个一样大就是没接上
/// （`MediaEncodingProfile.CreateMp4(quality)` 那套预设就会把设进去的码率吃掉）。
/// `--motion` 造两种极端内容，因为「哪种格式最小」完全看画面动不动。
/// </summary>
static class SizeLab
{
    /// <summary>
    /// 造两种极端内容各编一遍：几乎不动的画面，和大面积在动的画面。
    /// 「哪种格式最小」得看内容——静态画面里 WebP 帧间几乎没差别，压得极小；
    /// 动起来之后逐帧压缩的格式就按帧数线性涨，H.264 的帧间预测才开始占便宜。
    /// </summary>
    public static void Motion()
    {
        foreach (var (name, moving) in ((string, bool)[])[("几乎不动", false), ("大面积在动", true)])
        {
            var dir = Path.Combine(Path.GetTempPath(), "FlashTrans.motion." + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var paths = new List<string>();
            const int w = 640, h = 480, n = 60;
            var rnd = new Random(1234);

            // 底噪：不可压的花屏，模拟真实屏幕上的文字边缘
            var base_ = new byte[w * 4 * h];
            rnd.NextBytes(base_);
            for (var p = 3; p < base_.Length; p += 4) base_[p] = 0xFF;

            for (var i = 0; i < n; i++)
            {
                var buf = (byte[])base_.Clone();
                // 动的那块：moving 时扫过整幅，否则只挪一个小角
                var band = moving ? h : 24;
                var y0 = moving ? 0 : i % 8;
                for (var y = y0; y < Math.Min(h, y0 + band); y++)
                {
                    var shift = (i * 7) % 256;
                    for (var x = 0; x < w; x++)
                    {
                        var o = (y * w + x) * 4;
                        buf[o] = (byte)((x + shift) & 0xFF);
                        buf[o + 1] = (byte)((y + shift) & 0xFF);
                        buf[o + 2] = (byte)((x + y + shift) & 0xFF);
                    }
                }
                var f = Path.Combine(dir, $"m{i:D5}.png");
                new CapturedImage(w, h, buf).SavePng(f);
                paths.Add(f);
            }

            Console.WriteLine($"=== 造帧 {name}：{n} 帧 {w}×{h} 10 fps ===");
            try
            {
                foreach (var fmt in (RecordFormat[])[RecordFormat.Gif, RecordFormat.Webp, RecordFormat.Mp4])
                {
                    var r = Task.Run(() => AnimEncoder.SaveAsync(
                        paths, Path.Combine(dir, "m_" + fmt), 10, fmt)).GetAwaiter().GetResult();
                    Console.WriteLine($"  {fmt,-5} {r.Bytes / 1024.0,8:0} KB"
                                      + (r.FellBack ? $"  （退档：{r.FellBackWhy}）" : ""));
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    /// <summary>
    /// 找「MP4 一选就报错」的边界。自测里 48×32 和 640×480 都能编过，
    /// 用户真录一段却抛 COMException 0x80004005，说明是某个维度超了，
    /// 而不是「这台机器没有编码器」。这里按尺寸和帧率各扫一遍，看哪一格开始塌。
    /// </summary>
    public static void Mp4Lab()
    {
        Console.WriteLine($"编码器 DLL 在不在：{Mp4Encoder.Available}");

        (int W, int H)[] sizes =
        [
            (48, 32), (320, 240), (640, 480), (800, 600), (1024, 768),
            (1280, 720), (1366, 768), (1600, 900), (1920, 1080), (2560, 1440),
            (200, 1000), (1000, 200), (66, 66), (1918, 1078),
        ];

        foreach (var (w, h) in sizes) Try($"{w}×{h}", w, h, 10, 6);
        // 上面挂掉的三个（1366、66、1918）宽度都是「能被 2 整除但不能被 4 整除」。
        // 宽和高各扫一遍偶数，确认到底是哪一边有 4 的要求。
        foreach (var w in (int[])[636, 638, 640, 642, 644]) Try($"宽 {w}×480", w, 480, 10, 4);
        foreach (var h in (int[])[476, 478, 480, 482, 484]) Try($"高 640×{h}", 640, h, 10, 4);
        // 用户那台是 12 fps / 最长 121 秒，帧率单独再扫一遍
        foreach (var fps in (int[])[2, 12, 24, 30]) Try($"640×480 @{fps}fps", 640, 480, fps, 6);
        // 帧多会不会崩：121 秒 × 12 fps 是一千多帧
        foreach (var n in (int[])[1, 2, 300]) Try($"640×480 {n} 帧", 640, 480, 12, n);
    }

    /// <summary>
    /// 量「编出来的 MP4 是不是上下颠倒的」。
    ///
    /// 用户报「录制的视频是倒转过来的」。上下翻转这件事光看断言看不出来——
    /// 帧数、时长、盒子结构全对，只是每帧的行序反了。所以这里造一帧
    /// 上白下黑的图，编成 MP4 再把第一帧解回来，看白的那半落在上面还是下面。
    ///
    /// 亮度差比颜色可靠：H.264 是 4:2:0，色度要抽样，纯红纯蓝压完会偏；
    /// 黑白只动亮度分量，压多狠都还是一边亮一边暗。
    /// </summary>
    public static void FlipLab()
    {
        Console.WriteLine("=== 上下方向 ===");
        var dir = Path.Combine(Path.GetTempPath(), "FlashTrans.flip." + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            const int w = 320, h = 240;
            var paths = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                var f = Path.Combine(dir, $"f{i:D5}.png");
                FlipCheck.Corners(w, h).SavePng(f);
                paths.Add(f);
            }

            // 先确认造出来的 PNG 本身方向是对的，不然下面量到的翻转是这儿来的
            Console.WriteLine("  PNG  源帧      " + Describe(paths[0]));

            var mp4 = Path.Combine(dir, "flip.mp4");
            Task.Run(() => Mp4Encoder.SaveAsync(paths, mp4, 10)).GetAwaiter().GetResult();
            var got = Task.Run(() => FlipCheck.FirstMp4FrameAsync(mp4)).GetAwaiter().GetResult();
            Console.WriteLine("  MP4  解回来    " + Describe(got));

            // GIF / WebP 走的是另一条路（AnimEncoder、img2webp），顺手一起量，
            // 别假设它们没事
            foreach (var fmt in (RecordFormat[])[RecordFormat.Gif, RecordFormat.Webp])
            {
                var r = Task.Run(() => AnimEncoder.SaveAsync(
                    paths, Path.Combine(dir, "flip_" + fmt), 10, fmt)).GetAwaiter().GetResult();
                Console.WriteLine($"  {fmt,-4} 解回来    " + Describe(FlipCheck.Load(r.Path)));
            }
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            Console.WriteLine($"  FAIL  {inner.GetType().Name}: {inner.Message.Trim()}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static string Describe(string imgPath) => Describe(FlipCheck.Load(imgPath));

    static string Describe(System.Windows.Media.Imaging.BitmapSource img)
    {
        var (top, bottom) = FlipCheck.Halves(img);
        var (tl, tr, bl, br) = FlipCheck.Quadrants(img);
        var verdict = top > bottom + 40 ? "上亮下暗（对）"
            : bottom > top + 40 ? "上暗下亮（上下颠倒了）"
            : "上下分不出来";
        if (tl < tr - 20 || bl < br - 20) verdict += "，而且左右也镜像了";
        return $"{img.PixelWidth}×{img.PixelHeight} 上半亮度 {top:0} 下半 {bottom:0}"
               + $"（四角 {tl:0}/{tr:0}/{bl:0}/{br:0}）→ {verdict}";
    }

    /// <summary>造 n 帧 w×h 的噪声帧编一次 MP4，只报成没成。</summary>
    static void Try(string label, int w, int h, int fps, int n)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FlashTrans.mp4lab." + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var rnd = new Random(7);
            var paths = new List<string>();
            for (var i = 0; i < n; i++)
            {
                var buf = new byte[w * 4 * h];
                rnd.NextBytes(buf);
                for (var p = 3; p < buf.Length; p += 4) buf[p] = 0xFF;
                var f = Path.Combine(dir, $"f{i:D5}.png");
                new CapturedImage(w, h, buf).SavePng(f);
                paths.Add(f);
            }

            var r = Task.Run(() => Mp4Encoder.SaveAsync(paths, Path.Combine(dir, "out.mp4"), fps))
                        .GetAwaiter().GetResult();
            Console.WriteLine($"  OK    {label,-22} {r.Bytes / 1024.0,7:0} KB");
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            Console.WriteLine($"  FAIL  {label,-22} {inner.GetType().Name}: {inner.Message.Trim()}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    public static void Run()
    {
        foreach (var seconds in (int[])[2, 8])
        {
            var s = ScreenCapture.VirtualScreen();
            var region = new RECT
            {
                Left = s.Left + 8, Top = s.Top + 8,
                Right = Math.Min(s.Right, s.Left + 8 + 640),
                Bottom = Math.Min(s.Bottom, s.Top + 8 + 480),
            };

            Console.WriteLine($"=== 录 {seconds} 秒 10 fps 640×480 ===");
            var rec = Task.Run(() => RecordService.RunAsync(region, 10, seconds))
                          .GetAwaiter().GetResult();
            try
            {
                var fps = Math.Max(1, (int)Math.Round(rec.EffectiveFps));
                Console.WriteLine($"帧数 {rec.Paths.Count}，实测 {rec.EffectiveFps:0.#} fps");

                foreach (var fmt in (RecordFormat[])[RecordFormat.Gif, RecordFormat.Webp])
                {
                    var r = Task.Run(() => AnimEncoder.SaveAsync(
                        rec.Paths, Path.Combine(rec.Dir, "lab_" + fmt), fps, fmt))
                        .GetAwaiter().GetResult();
                    Console.WriteLine($"  {fmt,-5} {r.Bytes / 1024.0,8:0} KB");
                }

                // 直接给绝对码率，绕过 BitsPerPixel 的下限夹取——先确认这个旋钮
                // 到底有没有接上。接上了体积该跟着变，没接上就是四个一样大。
                foreach (var kbps in (uint[])[200, 600, 1500, 4000])
                {
                    Mp4Encoder.ForceBitrate = kbps * 1000;
                    var r = Task.Run(() => AnimEncoder.SaveAsync(
                        rec.Paths, Path.Combine(rec.Dir, $"lab_mp4_{kbps}"), fps, RecordFormat.Mp4))
                        .GetAwaiter().GetResult();
                    Console.WriteLine($"  MP4 {kbps,5} kbps {r.Bytes / 1024.0,8:0} KB"
                                      + $"  实际 {r.Bytes * 8.0 / Math.Max(1, seconds) / 1000:0} kbps");
                }
                Mp4Encoder.ForceBitrate = null;
            }
            finally { rec.Cleanup(); }
        }
    }
}
