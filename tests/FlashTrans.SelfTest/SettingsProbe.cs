using System.Reflection;
using System.Text.Json;
using FlashTrans.Core;
using FlashTrans.Services;

namespace FlashTrans.SelfTest;

/// <summary>
/// 设置本身的自测，回答的是「我在设置里改的东西，到底存下来没有、生效没有」。
///
/// 起因：开机自启勾了却不启动。真实原因是 Run 项里存的绝对路径在程序换目录之后就废了，
/// 而判断「开着没有」只看那条命令里含不含 FlashTrans 这个词，于是设置里永远显示开着。
/// </summary>
static class SettingsProbe
{
    public static void RunAll(Action<string, Action> step)
    {
        step("设置：每个字段都能存下来再读回来", RoundTrip);
        step("设置：算出来的字段不落盘", NoComputedFields);
        step("设置：Normalize 不动设置页里能填到的边界值", BoundsMatchUi);
        step("设置：Normalize 反复跑结果不变", NormalizeIsStable);
        step("开机自启：从 Run 项的命令里取出 exe 路径", ParseCommand);
        step("开机自启：程序换了目录要认出来路径已经失效", DetectsMovedExe);
        step("开机自启：自测程序不许去改用户的 Run 项", RefusesFromForeignProcess);
    }

    /// <summary>
    /// EnabledProviders 这种算出来的属性不该出现在配置文件里：
    /// 它会把整份翻译源（含加密后的密钥）多存一遍，读回来又没人要。
    /// </summary>
    static void NoComputedFields()
    {
        var json = JsonSerializer.Serialize(AppSettings.CreateDefault(), SettingsJson.Default.AppSettings);
        if (json.Contains("enabledProviders", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("enabledProviders 被写进配置文件了");
    }

    // ---------------------------------------------------------------- 存取

    /// <summary>
    /// 把每个字段都改成一个跟默认值不同的值，序列化再读回来，逐个字段比。
    /// 只读属性、漏在源生成器外面的字段，都会在这儿露出来 —— 那意味着
    /// 用户在设置里改了它，重启就白改。
    /// </summary>
    static void RoundTrip()
    {
        var s = AppSettings.CreateDefault();
        var touched = new List<PropertyInfo>();

        foreach (var p in Writable())
        {
            var value = Distinct(p, p.GetValue(s));
            if (value is null) continue;
            p.SetValue(s, value);
            touched.Add(p);
        }
        if (touched.Count < 40)
            throw new InvalidOperationException($"只改到了 {touched.Count} 个字段，探针自己写坏了");

        var json = JsonSerializer.Serialize(s, SettingsJson.Default.AppSettings);
        var back = JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings)
                   ?? throw new InvalidOperationException("读回来是 null");

        var lost = touched.Where(p => !Same(p.GetValue(s), p.GetValue(back)))
                          .Select(p => $"{p.Name}（存 {Show(p.GetValue(s))}，读回 {Show(p.GetValue(back))}）")
                          .ToList();
        if (lost.Count > 0)
            throw new InvalidOperationException("这些字段存不下来：" + string.Join("；", lost));
    }

    static IEnumerable<PropertyInfo> Writable() =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(AppSettings.Version));

    /// <summary>给一个跟现值不同的值。给不出来（比如翻译源列表）就返回 null，跳过。</summary>
    static object? Distinct(PropertyInfo p, object? current)
    {
        var t = p.PropertyType;
        if (t == typeof(bool)) return !(bool)current!;
        if (t == typeof(int)) return (int)current! + 7;
        if (t == typeof(double)) return (double)current! + 1.5;
        if (t == typeof(string)) return (string?)current + "-x";
        if (t == typeof(List<string>)) return new List<string> { "aa", "bb" };
        if (t.IsEnum)
        {
            var values = Enum.GetValues(t).Cast<object>().ToList();
            return values.FirstOrDefault(v => !Equals(v, current)) ?? current;
        }
        return null;   // List<ProviderConfig> 之类，另有用例覆盖
    }

    static bool Same(object? a, object? b)
    {
        if (a is List<string> la && b is List<string> lb) return la.SequenceEqual(lb, StringComparer.Ordinal);
        return Equals(a, b);
    }

    static string Show(object? v) => v is List<string> l ? "[" + string.Join(",", l) + "]" : v?.ToString() ?? "null";

    // ---------------------------------------------------------------- 取值范围

    /// <summary>
    /// 设置页能填到的边界值，Normalize 必须原样放过。
    /// 两边各写一遍数字就会漂：输入框允许 120ms，而 Normalize 夹到 150 —— 用户填的 120
    /// 当场生效，下次启动却变回 150，看起来就是「设置没保存」。
    /// </summary>
    static void BoundsMatchUi()
    {
        Check("输入停顿延迟", AppSettings.MinTypeDelayMs, AppSettings.MaxTypeDelayMs,
            (s, v) => s.TypeDelayMs = v, s => s.TypeDelayMs);
        Check("画笔粗细", (int)CaptureLimits.MinPenWidth, (int)CaptureLimits.MaxPenWidth,
            (s, v) => s.CapturePenWidth = v, s => (int)s.CapturePenWidth);
        Check("文字字号", (int)CaptureLimits.MinFontSize, (int)CaptureLimits.MaxFontSize,
            (s, v) => s.CaptureFontSize = v, s => (int)s.CaptureFontSize);
        Check("马赛克格子", CaptureLimits.MinMosaicBlock, CaptureLimits.MaxMosaicBlock,
            (s, v) => s.CaptureMosaicBlock = v, s => s.CaptureMosaicBlock);
        Check("录制帧率", RecordService.MinFps, RecordService.MaxFps,
            (s, v) => s.RecordFps = v, s => s.RecordFps);
        Check("录制时长", RecordService.MinSeconds, RecordService.MaxSeconds,
            (s, v) => s.RecordMaxSeconds = v, s => s.RecordMaxSeconds);

        static void Check(string what, int min, int max, Action<AppSettings, int> set, Func<AppSettings, int> get)
        {
            foreach (var v in new[] { min, max })
            {
                var s = AppSettings.CreateDefault();
                set(s, v);
                SettingsService.Normalize(s);
                if (get(s) != v)
                    throw new InvalidOperationException($"{what} 填 {v} 被改成了 {get(s)}");
            }
        }
    }

    static void NormalizeIsStable()
    {
        var s = AppSettings.CreateDefault();
        SettingsService.Normalize(s);
        var once = JsonSerializer.Serialize(s, SettingsJson.Default.AppSettings);
        SettingsService.Normalize(s);
        if (JsonSerializer.Serialize(s, SettingsJson.Default.AppSettings) != once)
            throw new InvalidOperationException("Normalize 跑第二遍又改了东西");
    }

    // ---------------------------------------------------------------- 开机自启

    static void ParseCommand()
    {
        Same("\"C:\\App\\FlashTrans.exe\" --tray", "C:\\App\\FlashTrans.exe");
        Same("C:\\App\\FlashTrans.exe --tray", "C:\\App\\FlashTrans.exe");
        Same("C:\\App\\FlashTrans.exe", "C:\\App\\FlashTrans.exe");
        if (StartupService.PathOf("") is not null) throw new InvalidOperationException("空命令该给 null");
        if (StartupService.PathOf(null) is not null) throw new InvalidOperationException("null 该给 null");

        static void Same(string command, string want)
        {
            var got = StartupService.PathOf(command);
            if (!string.Equals(got, want, StringComparison.Ordinal))
                throw new InvalidOperationException($"{command} → {got}，应该是 {want}");
        }
    }

    /// <summary>用户碰上的就是这个：升级换了个带版本号的文件夹，Run 项还指着老路径。</summary>
    static void DetectsMovedExe()
    {
        const string now = @"C:\App\FlashTrans-1.7.7\FlashTrans.exe";
        const string old = @"C:\App\FlashTrans-1.7.6\FlashTrans.exe";

        if (!StartupService.Aligned($"\"{now}\" --tray", now))
            throw new InvalidOperationException("指着自己却说不一致");
        if (StartupService.Aligned($"\"{old}\" --tray", now))
            throw new InvalidOperationException("路径已经变了还说一致 —— 这条正是开机启不动的原因");
        if (StartupService.Aligned($"\"{now}\"", now))
            throw new InvalidOperationException("少了 --tray 也算一致，开机会弹出主窗口");
        if (StartupService.Aligned(null, now))
            throw new InvalidOperationException("压根没这条却说一致");

        // 同一个 exe 的不同写法不该被当成「变了」，否则每次启动都白写一次注册表
        if (!StartupService.Aligned("\"C:\\App\\sub\\..\\FlashTrans.exe\" --tray", @"C:\App\FlashTrans.exe"))
            throw new InvalidOperationException("同一个路径换个写法就认不出了");
        if (!StartupService.Aligned("\"c:\\app\\flashtrans.exe\" --tray", @"C:\App\FlashTrans.exe"))
            throw new InvalidOperationException("大小写不同就认不出了");
    }

    /// <summary>
    /// 自测程序的 bin 目录里也躺着一份 FlashTrans.exe。Sync 要是不认「谁在跑」，
    /// 跑一次自测就把用户的开机自启指到测试输出目录去了。
    /// </summary>
    static void RefusesFromForeignProcess()
    {
        var before = StartupService.CurrentCommand();
        if (StartupService.Sync(true))
            throw new InvalidOperationException("自测进程居然改动了 Run 项");
        if (StartupService.CurrentCommand() != before)
            throw new InvalidOperationException("Run 项被自测改了：" + StartupService.CurrentCommand());
    }
}
