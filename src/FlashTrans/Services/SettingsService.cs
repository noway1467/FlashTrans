using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlashTrans.Core;

namespace FlashTrans.Services;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    // 窗口位置未设置时是 NaN，得允许写成 "NaN"
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJson : JsonSerializerContext;

/// <summary>设置读写。支持便携模式（exe 同目录放 portable.txt）。密钥用 DPAPI 加密。</summary>
public sealed class SettingsService
{
    public static SettingsService Instance { get; } = new();

    public AppSettings Current { get; private set; } = new();
    public string ConfigDir { get; }
    public string ConfigPath => Path.Combine(ConfigDir, "settings.json");

    public event Action<AppSettings>? Changed;

    SettingsService()
    {
        var exeDir = AppContext.BaseDirectory;
        var portable = File.Exists(Path.Combine(exeDir, "portable.txt"));
        ConfigDir = portable
            ? Path.Combine(exeDir, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlashTrans");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var s = JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings);
                if (s is not null)
                {
                    Decrypt(s);
                    var migrated = Migrate(s);
                    Normalize(s);
                    Current = s;
                    if (migrated) Save();   // 迁移结果落盘，否则每次启动都要再补一遍
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("设置读取失败，使用默认值：" + ex.Message);
            TryBackupBroken();
        }
        Current = AppSettings.CreateDefault();
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            // 加密副本，不影响内存中的明文
            var copy = JsonSerializer.Deserialize(
                JsonSerializer.Serialize(Current, SettingsJson.Default.AppSettings),
                SettingsJson.Default.AppSettings)!;
            Encrypt(copy);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copy, SettingsJson.Default.AppSettings));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("设置保存失败：" + ex.Message);
        }
    }

    /// <summary>提交修改并广播（设置窗口点确定时调用）。</summary>
    public void Apply(AppSettings s)
    {
        Normalize(s);
        Current = s;
        Save();
        Net.Configure(string.IsNullOrWhiteSpace(s.Proxy) ? null : s.Proxy);
        Changed?.Invoke(s);
    }

    /// <summary>只改了几个字段（比如语言/置顶），保存并广播。</summary>
    public void Touch()
    {
        Save();
        Changed?.Invoke(Current);
    }

    /// <summary>
    /// 老配置补上新增的默认源。改了返回 true。
    /// 不这么做的话，新增的免费源只有全新安装的人能看到，老用户升级后压根不知道它存在
    /// —— 而 CreateDefault 只在配置不存在时才跑。
    /// </summary>
    public static bool Migrate(AppSettings s)
    {
        var changed = false;

        // v1 -> v2：加上腾讯交互翻译（免费、无需 Key、国内直连）
        if (s.Version < 2)
        {
            if (s.Providers.All(p => p.Kind != ProviderKind.TranSmart))
            {
                var cfg = ProviderConfig.Create(ProviderKind.TranSmart);
                // 插在最后一个免费源后面，别打乱用户自己排的顺序，也别抢主用源
                var at = s.Providers.FindLastIndex(p => !ProviderMeta.Get(p.Kind).NeedsKey);
                s.Providers.Insert(at < 0 ? s.Providers.Count : at + 1, cfg);
                Log.Warn("配置迁移：已添加「腾讯交互翻译（免费）」");
            }
            s.Version = 2;
            changed = true;
        }

        // v2 -> v3：文字标注的字号从画笔粗细里独立出来。
        // 不补这一段的话老用户升上来会发现文字标注忽然换了一号大小——
        // 他们的粗细是 3（字号 15），而新字段的默认值是 20。
        if (s.Version < 3)
        {
            s.CaptureFontSize = CaptureLimits.FontSizeForWidth(s.CapturePenWidth);
            s.Version = 3;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 把配置里的数值夹回可用范围，缺的补上。
    /// 公开是为了能单独验：工具条上能调到的范围必须跟这儿夹的一致，
    /// 不然用户调好的值下次启动会被悄悄改回去。
    /// </summary>
    public static void Normalize(AppSettings s)
    {
        if (s.Providers.Count == 0) s.Providers.AddRange(AppSettings.CreateDefault().Providers);
        foreach (var p in s.Providers)
        {
            if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Guid.NewGuid().ToString("N")[..8];
            if (p.TimeoutMs is < 1000 or > 120000) p.TimeoutMs = 6000;
        }
        // Id 去重
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in s.Providers)
            if (!seen.Add(p.Id)) p.Id = Guid.NewGuid().ToString("N")[..8];

        if (s.Find(s.PrimaryProviderId) is null)
            s.PrimaryProviderId = s.EnabledProviders.FirstOrDefault()?.Id ?? s.Providers[0].Id;

        if (s.MultiTargets.Count == 0) s.MultiTargets = [s.TargetLang];
        if (string.IsNullOrWhiteSpace(s.TargetLang)) s.TargetLang = "zh-CN";
        s.FontSize = Math.Clamp(s.FontSize, 10, 28);
        s.Opacity = Math.Clamp(s.Opacity, 0.4, 1.0);
        s.MaxParallel = Math.Clamp(s.MaxParallel, 1, 16);
        s.PopupWidth = Math.Clamp(s.PopupWidth, 260, 900);
        // 上限放到 2400 是为了 4K / 竖屏；实际显示时 PopupWindow 还会按当前屏幕的工作区收一次，
        // 所以这里夹得比设置页的滑块宽，不会把用户调好的值悄悄改小。
        s.PopupMaxHeight = Math.Clamp(s.PopupMaxHeight, 180, 2400);
        s.WinWidth = Math.Clamp(s.WinWidth, 380, 2400);
        s.WinHeight = Math.Clamp(s.WinHeight, 260, 1800);
        s.TypeDelayMs = Math.Clamp(s.TypeDelayMs, 150, 3000);
        s.CacheSize = Math.Clamp(s.CacheSize, 0, 20000);
        s.CacheTtlHours = Math.Clamp(s.CacheTtlHours, 1, 168);
        // 工具条上能调到的范围就是这儿夹的范围，两边必须一致，
        // 否则用户在工具条上调好的值下次启动会被悄悄改回去。
        s.CapturePenWidth = CaptureLimits.ClampPenWidth(s.CapturePenWidth);
        s.CaptureFontSize = CaptureLimits.ClampFontSize(s.CaptureFontSize);
        s.CaptureMosaicBlock = CaptureLimits.ClampMosaicBlock(s.CaptureMosaicBlock);
        s.RecordFps = RecordService.ClampFps(s.RecordFps);
        s.RecordMaxSeconds = RecordService.ClampSeconds(s.RecordMaxSeconds);
    }

    static void Encrypt(AppSettings s)
    {
        foreach (var p in s.Providers)
            foreach (var key in p.Options.Keys.ToList())
                if (ProviderMeta.IsSecret(key) && !string.IsNullOrEmpty(p.Options[key]))
                    p.Options[key] = Dpapi.Protect(p.Options[key]);
    }

    static void Decrypt(AppSettings s)
    {
        foreach (var p in s.Providers)
            foreach (var key in p.Options.Keys.ToList())
                if (ProviderMeta.IsSecret(key) && !string.IsNullOrEmpty(p.Options[key]))
                    p.Options[key] = Dpapi.Unprotect(p.Options[key]);
    }

    void TryBackupBroken()
    {
        try
        {
            if (File.Exists(ConfigPath))
                File.Move(ConfigPath, ConfigPath + ".bad", overwrite: true);
        }
        catch { /* ignore */ }
    }
}
