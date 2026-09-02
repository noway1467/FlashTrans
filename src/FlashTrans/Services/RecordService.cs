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
    /// <summary>暂停着太久没动静，自己收了。</summary>
    PausedTooLong,
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
    /// <summary>暂停了几次、一共暂停了多久。只用来在提示里说一声。</summary>
    public int Pauses { get; init; }
    public TimeSpan PausedFor { get; init; }
    /// <summary>音频文件路径（录制 MP4 且开启了音频录制时才有）。</summary>
    public string? AudioPath { get; init; }

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
    /// 暂停着最多挂多久（分钟）。暂停不吃时长预算，所以没有这道闸的话，
    /// 按了暂停就走开等于把临时帧和这个循环无限期留在那儿。
    /// </summary>
    public const int MaxPausedMinutes = 10;

    /// <summary>
    /// 等一小段。上限压在 200ms 是为了暂停/停止能跟手：
    /// 低帧率下一帧的间隔可能是 500ms，睡整段的话按了暂停要等半秒才有反应。
    /// </summary>
    static Task NapAsync(double ms) => Task.Delay((int)Math.Clamp(ms, 1, 200));

    /// <summary>
    /// 把选区的宽高吸到 4 的倍数（各最多少 3 个像素）。
    ///
    /// 4:2:0 色度采样本身只要偶数，但系统那个 H.264 编码器实际要 4 的倍数，
    /// 差 2 就抛 0x80004005，见 <see cref="Mp4Encoder.Align4"/> 上的实测数据。
    /// 在这儿统一吸掉，而不是留给 MP4 那条路自己切：三种格式录到的画面得是
    /// 同一块区域，不然同一次录制换个格式尺寸就变了；而且提前吸掉的话
    /// 抓到的帧本身就是对的尺寸，MP4 那边不用再逐帧裁一刀。
    /// 少几个像素肉眼看不出来，拖框本来也不是像素级精确的。
    /// </summary>
    internal static RECT Snap4(RECT r)
    {
        var w = Math.Max(0, r.Right - r.Left);
        var h = Math.Max(0, r.Bottom - r.Top);
        return new RECT
        {
            Left = r.Left,
            Top = r.Top,
            Right = r.Left + Mp4Encoder.Align4(w),
            Bottom = r.Top + Mp4Encoder.Align4(h),
        };
    }

    /// <summary>
    /// 在 region（屏幕物理像素）上录。
    /// onProgress 每抓一帧调一次，参数是帧数和已经录了多久（不含暂停掉的时间）。
    /// cancelled 返回 true 就停；paused 返回 true 就挂着不抓帧。
    /// captureAudio 为 true 时同时录制系统音频（只对 MP4 有效）。
    /// muted 每拍问一次，返回 true 就把音频拧成无声（照样写，不然音画会错位）。
    /// </summary>
    public static async Task<RecordFrames> RunAsync(
        RECT region, int fps, int maxSeconds,
        Action<int, TimeSpan>? onProgress = null,
        Func<bool>? cancelled = null,
        Func<bool>? paused = null,
        int? maxPausedMs = null,
        Func<RECT, CapturedImage?>? capture = null,
        bool captureAudio = false,
        Func<bool>? muted = null)
    {
        // 默认那道闸是 10 分钟，自测等不了；留个口子让它传小值进来。
        var pauseLimit = maxPausedMs ?? MaxPausedMinutes * 60_000;
        fps = ClampFps(fps);
        maxSeconds = ClampSeconds(maxSeconds);
        region = Snap4(region);

        var dir = Path.Combine(Path.GetTempPath(), "FlashTrans.rec." + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var paths = new List<string>();
        var interval = 1000.0 / fps;
        var maxFrames = Math.Max(1, fps * maxSeconds);
        var stop = RecordStop.Limit;
        double firstAt = 0, lastAt = 0;

        // 音频录制
        AudioCapture? audioCapture = null;
        string? audioPath = null;
        if (captureAudio)
        {
            try
            {
                audioPath = Path.Combine(dir, "audio.wav");
                audioCapture = new AudioCapture();
                var audioErr = await audioCapture.StartAsync(audioPath);
                if (audioErr is not null)
                {
                    Log.Warn("音频录制启动失败：" + audioErr);
                    audioCapture.Dispose();
                    audioCapture = null;
                    audioPath = null;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("音频录制启动异常：" + ex.Message);
                audioCapture?.Dispose();
                audioCapture = null;
                audioPath = null;
            }
        }

        // 所有调度都走「活动时钟」= 墙上时钟 - 暂停掉的时间。这样两件事同时成立：
        // 回放是连续的（暂停期间不抓帧，动图里不会多出一段静止画面），
        // 暂停也不吃时长预算（挂着 5 分钟回来，那 30 秒还剩多少就是多少）。
        var sw = Stopwatch.StartNew();
        double pausedMs = 0;
        double pauseAt = -1;   // >= 0 表示正暂停着，值是按下暂停那一刻的墙上时刻
        var pauses = 0;

        while (paths.Count < maxFrames)
        {
            if (cancelled?.Invoke() == true) { stop = RecordStop.Stopped; break; }

            // 时长也要看，不能只数帧。跟不上目标帧率时（区域大、机器忙）帧数攒得慢，
            // 光等 maxFrames 的话「最长 30 秒」会变成录满 300 帧、实际过了 75 秒。
            // 用活动时钟，所以暂停掉的时间照样不算。
            if (sw.Elapsed.TotalMilliseconds - pausedMs >= maxSeconds * 1000.0) break;

            var wantPause = paused?.Invoke() == true;
            if (wantPause)
            {
                // 刚按下：记下时刻，从这里开始的墙上时间都要从活动时钟里刨掉。
                if (pauseAt < 0) { pauseAt = sw.Elapsed.TotalMilliseconds; pauses++; }
                else if (sw.Elapsed.TotalMilliseconds - pauseAt > pauseLimit)
                {
                    // 按了暂停就走开了。收摊，已经录到的帧照样交出去。
                    pausedMs += sw.Elapsed.TotalMilliseconds - pauseAt;
                    pauseAt = -1;
                    stop = RecordStop.PausedTooLong;
                    break;
                }
                // 画面不抓了，声音也得停住，不然音频比视频长一截，恢复之后全错位。
                audioCapture?.SetPaused(true);
                // 暂停时用固定的短间隔轮询，跟帧率无关——2 fps 下也要一按就恢复。
                await Task.Delay(50);
                continue;
            }
            if (pauseAt >= 0)
            {
                // 刚恢复：把这一段计进暂停总时长。
                pausedMs += sw.Elapsed.TotalMilliseconds - pauseAt;
                pauseAt = -1;
                audioCapture?.SetPaused(false);
            }

            // 静音是每拍问一次的：用户录到一半点了那个按钮，这里才看得见。
            if (audioCapture is not null && muted is not null)
                audioCapture.SetMuted(muted());

            // 按绝对时刻等，不是「每次睡 interval」——后者会把每帧的处理时间
            // 累加进去，录 30 秒实际只录到 20 秒的内容。
            var wait = paths.Count * interval - (sw.Elapsed.TotalMilliseconds - pausedMs);
            if (wait > 1)
            {
                // 分段睡，中间回来看一眼暂停/停止有没有按。
                await NapAsync(wait);
                continue;
            }

            var at = sw.Elapsed.TotalMilliseconds - pausedMs;
            CapturedImage? img;
            try
            {
                img = await Task.Run(() => (capture ?? ScreenCapture.Grab)(region));
            }
            catch (Exception ex)
            {
                Log.Warn("录制抓帧失败：" + ex.Message);
                break;
            }
            if (img is null) break;

            var path = Path.Combine(dir, $"f{paths.Count:D5}.png");
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
            onProgress?.Invoke(paths.Count, TimeSpan.FromMilliseconds(at));
        }

        // 循环可能是在暂停中途跳出来的（用户暂停着直接按 Esc），那一段也得算上。
        if (pauseAt >= 0) pausedMs += sw.Elapsed.TotalMilliseconds - pauseAt;
        sw.Stop();

        // 停止音频录制
        if (audioCapture is not null)
        {
            try
            {
                var audioErr = await audioCapture.StopAsync();
                if (audioErr is not null)
                {
                    Log.Warn("音频录制停止失败：" + audioErr);
                    audioPath = null;   // 文件可能是坏的，不交出去
                }
            }
            catch (Exception ex)
            {
                Log.Warn("音频录制停止异常：" + ex.Message);
                audioPath = null;
            }
            finally
            {
                audioCapture.Dispose();
            }
        }

        var active = TimeSpan.FromMilliseconds(Math.Max(0, sw.Elapsed.TotalMilliseconds - pausedMs));
        var pausedFor = TimeSpan.FromMilliseconds(pausedMs);

        if (paths.Count == 0)
            return new RecordFrames(dir, paths, TimeSpan.Zero, fps, RecordStop.Failed)
            { Pauses = pauses, PausedFor = pausedFor, AudioPath = audioPath };

        return new RecordFrames(dir, paths, active,
            Effective(paths.Count, firstAt, lastAt, fps), stop)
        { Pauses = pauses, PausedFor = pausedFor, AudioPath = audioPath };
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
