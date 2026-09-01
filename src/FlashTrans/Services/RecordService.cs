using System.Diagnostics;
using System.IO;
using FlashTrans.Interop;

namespace FlashTrans.Services;

public enum RecordStop
{
    /// <summary>用户自己停的。</summary>
    Stopped,
    /// <summary>到时长上限了。</summary>
    Limit,
    /// <summary>一帧都没抓到。</summary>
    Failed,
}

/// <summary>
/// 录下来的一串帧。Dir 是临时目录，编码完要调 Cleanup 删掉。
///
/// EffectiveFps 是实测帧率，不一定等于设置里那个：抓一帧要走 BitBlt 加 PNG 编码，
/// 区域大的时候跟不上目标帧率。编码时必须用实测值——拿目标值去写延时的话，
/// 实际 4 fps 的帧按 10 fps 播，出来的动图是 2.5 倍快放。
/// </summary>
public sealed record RecordFrames(
    string Dir, List<string> Paths, TimeSpan Elapsed, double EffectiveFps, RecordStop Stopped)
{
    public void Cleanup()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
        catch (Exception ex) { Log.Warn("删录制临时文件失败：" + ex.Message); }
    }
}

/// <summary>
/// 录制动图：在一块固定区域上按节拍反复抓屏，每帧落成一个临时 PNG。
///
/// 为什么落盘而不攒在内存里：一帧 1920×1080 的 BGRA 是 8MB，10 fps 录 30 秒
/// 就是 2.4GB。PNG 落盘之后每帧几十到几百 KB，而且 img2webp 本来就要文件当输入。
/// </summary>
public static class RecordService
{
    /// <summary>帧率允许的范围。上限压在 30：再高界面自己就成瓶颈了，见 EffectiveFps。</summary>
    public const int MinFps = 2;
    public const int MaxFps = 30;

    /// <summary>时长上限允许的范围（秒）。</summary>
    public const int MinSeconds = 2;
    public const int MaxSeconds = 300;

    public static int ClampFps(int v) => Math.Clamp(v, MinFps, MaxFps);
    public static int ClampSeconds(int v) => Math.Clamp(v, MinSeconds, MaxSeconds);

    /// <summary>
    /// 在 region（屏幕物理像素）上录。
    /// onProgress 每抓一帧调一次，参数是帧数和已经录了多久。
    /// cancelled 返回 true 就停。
    /// </summary>
    public static async Task<RecordFrames> RunAsync(
        RECT region, int fps, int maxSeconds,
        Action<int, TimeSpan>? onProgress = null, Func<bool>? cancelled = null)
    {
        fps = ClampFps(fps);
        maxSeconds = ClampSeconds(maxSeconds);

        var dir = Path.Combine(Path.GetTempPath(), "FlashTrans.rec." + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var paths = new List<string>();
        var interval = 1000.0 / fps;
        var maxFrames = Math.Max(1, fps * maxSeconds);
        var stop = RecordStop.Limit;
        double firstAt = 0, lastAt = 0;
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < maxFrames; i++)
        {
            if (cancelled?.Invoke() == true) { stop = RecordStop.Stopped; break; }

            // 按绝对时刻等，不是「每次睡 interval」——后者会把每帧的处理时间
            // 累加进去，录 30 秒实际只录到 20 秒的内容。
            var wait = i * interval - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) await Task.Delay((int)wait);

            var at = sw.Elapsed.TotalMilliseconds;
            CapturedImage? img;
            try
            {
                img = await Task.Run(() => ScreenCapture.Grab(region));
            }
            catch (Exception ex)
            {
                Log.Warn("录制抓帧失败：" + ex.Message);
                break;
            }
            if (img is null) break;

            var path = Path.Combine(dir, $"f{i:D5}.png");
            try
            {
                await Task.Run(() => img.SavePng(path));
            }
            catch (Exception ex)
            {
                // 磁盘满或者临时目录没权限。已经录到的帧照样交出去，别整个丢掉。
                Log.Warn("录制存帧失败：" + ex.Message);
                break;
            }

            if (paths.Count == 0) firstAt = at;
            lastAt = at;
            paths.Add(path);
            onProgress?.Invoke(paths.Count, sw.Elapsed);
        }

        sw.Stop();
        if (paths.Count == 0)
            return new RecordFrames(dir, paths, TimeSpan.Zero, fps, RecordStop.Failed);

        return new RecordFrames(dir, paths, sw.Elapsed,
            Effective(paths.Count, firstAt, lastAt, fps), stop);
    }

    /// <summary>
    /// 实测帧率：帧间隔取「第一帧到最后一帧」的平均，除以间隔数（帧数 - 1）。
    /// 只有一帧时没有间隔可言，用目标值。
    /// </summary>
    internal static double Effective(int count, double firstMs, double lastMs, int fps)
    {
        var span = lastMs - firstMs;
        if (count < 2 || span <= 0) return fps;
        return Math.Clamp((count - 1) * 1000.0 / span, 1, MaxFps);
    }
}
