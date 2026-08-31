using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FlashTrans.Services;

namespace FlashTrans;

public partial class App : Application
{
    const string MutexName = "FlashTrans.SingleInstance.v1";
    const string WakeName = "FlashTrans.Wake.v1";

    static Mutex? _mutex;
    AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：已有进程在跑就叫它显示窗口，自己立刻退出
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            try
            {
                using var wake = EventWaitHandle.OpenExisting(WakeName);
                wake.Set();
            }
            catch { /* 对方可能正在退出 */ }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("界面线程未处理异常", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("后台未处理异常", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warn("任务异常：" + args.Exception.Message);
            args.SetObserved();
        };

        SettingsService.Instance.Load();
        var s = SettingsService.Instance.Current;
        ThemeService.Apply(s);
        Core.Net.Configure(string.IsNullOrWhiteSpace(s.Proxy) ? null : s.Proxy);

        _host = new AppHost();
        _host.Start(startHidden: s.StartMinimized || e.Args.Contains("--tray"));

        ListenForWake();

        if (e.Args.Contains("--benchmark")) ReportStartup();
    }

    /// <summary>诊断用：把「进程启动 → 可响应热键」的耗时写到日志，然后退出。</summary>
    void ReportStartup()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var ready = (DateTime.Now - proc.StartTime).TotalMilliseconds;

        Dispatcher.BeginInvoke(() =>
        {
            var idle = (DateTime.Now - proc.StartTime).TotalMilliseconds;
            proc.Refresh();
            Log.Warn($"[benchmark] 就绪 {ready:F0}ms · 空闲 {idle:F0}ms · " +
                     $"工作集 {proc.WorkingSet64 / 1024 / 1024}MB");
            // 启动路径上不该加载的东西一旦被拖进来，就是这里先看出来。
            // Windows SDK 投影有 24MB，只有截图识别用得到，按下热键前不该出现。
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            var heavy = loaded.Where(a => a.GetName().Name is "Microsoft.Windows.SDK.NET" or "WinRT.Runtime")
                              .Select(a => a.GetName().Name).ToArray();
            Log.Warn($"[benchmark] 已加载程序集 {loaded.Length} 个" +
                     (heavy.Length == 0 ? "，未碰 WinRT 投影" : "，含 " + string.Join(" + ", heavy)));
            Shutdown();
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>第二个实例启动时把主窗口叫出来。</summary>
    void ListenForWake()
    {
        var wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                wake.WaitOne();
                Dispatcher.BeginInvoke(() => _host?.ShowMainWindow(focusInput: true), DispatcherPriority.Normal);
            }
        }) { IsBackground = true, Name = "FlashTrans.Wake" };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        base.OnExit(e);
    }
}
