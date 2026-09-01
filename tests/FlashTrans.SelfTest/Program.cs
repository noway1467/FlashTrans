using System.Windows;
using FlashTrans;
using FlashTrans.Core;
using FlashTrans.Services;

namespace FlashTrans.SelfTest;

/// <summary>
/// 在真实 WPF 环境里把每个窗口和每个设置页都构造一遍：
/// XAML 里写错的 StaticResource、样式 TargetType 不匹配这类问题只有运行期才暴露。
/// </summary>
static class Program
{
    static int _fail;

    [STAThread]
    static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        SettingsService.Instance.Load();
        var s = SettingsService.Instance.Current;
        ThemeService.Apply(s);

        Step("加载深色主题", () => ThemeService.ApplyTheme(AppTheme.Dark));
        Step("加载浅色主题", () => ThemeService.ApplyTheme(AppTheme.Light));
        Step("恢复深色主题", () =>
        {
            ThemeService.ApplyTheme(AppTheme.Dark);
            ThemeService.ApplyAccent(s.AccentColor);
        });

        CacheProbe.RunAll(Step);
        HttpVersionProbe.RunAll(Step);

        var host = new AppHost();

        UiProbe.RunAll(host, Step);
        OcrProbe.RunAll(Step);
        LongShotProbe.RunAll(Step);
        RecordProbe.RunAll(Step);

        if (args.Contains("--net")) NetProbe.Run(Step);
        if (args.Contains("--timing")) TimingProbe.Run();
        if (args.Contains("--shot")) ShotProbe.Run("shots", host);

        Console.WriteLine(_fail == 0 ? "\n全部通过" : $"\n失败 {_fail} 项");
        app.Shutdown();
        return _fail == 0 ? 0 : 1;
    }

    static void Step(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"  OK   {name}");
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine($"  FAIL {name}");
            Console.WriteLine($"       {ex.GetType().Name}: {ex.Message}");
            var inner = ex.InnerException;
            while (inner is not null)
            {
                Console.WriteLine($"       -> {inner.GetType().Name}: {inner.Message}");
                inner = inner.InnerException;
            }
            // 只留本项目的帧：WPF 的栈很长，中间全是框架内部调用，看不出问题在哪
            foreach (var line in (ex.StackTrace ?? "").Split('\n')
                     .Where(l => l.Contains("FlashTrans")).Take(6))
                Console.WriteLine("       " + line.Trim());
        }
    }
}
