using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using FlashTrans.Core;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

static class StartupProbe
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static void RunAll(Action<string, Action> step)
    {
        step("自启动：旧配置保留原权限，新开关往返保存", () =>
        {
            var old = JsonSerializer.Deserialize("{\"version\":16,\"runAtStartup\":true}", SettingsJson.Default.AppSettings)!;
            Check(SettingsService.Migrate(old) && old.RunAtStartup && !old.StartupAsAdmin, "升级改变了原自启或权限");
            old.StartupAsAdmin = true;
            var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(old, SettingsJson.Default.AppSettings), SettingsJson.Default.AppSettings)!;
            Check(back.StartupAsAdmin && back.RunAtStartup && !SettingsService.Migrate(back), "管理员开关保存或迁移不稳定");
            Check(typeof(App).Assembly.GetName().Version == new Version(1, 9, 10, 0), "版本不是 1.9.10");
        });
        step("自启动：当前用户、管理员权限、托盘参数及常驻条件", () =>
        {
            const string exe = @"E:\中文 & 空格\FlashTrans.exe";
            var sid = StartupService.CurrentUserSid;
            var xml = StartupService.BuildTaskXml(exe, sid);
            Check(StartupService.TaskAligned(xml, exe, sid), "任务参数不完整");
            Check(!StartupService.TaskAligned(xml, exe + ".old", sid), "未识别失效路径");
            Check(!StartupService.TaskAligned(xml, exe, "S-1-5-18"), "接受了其他账号的任务");
            foreach (var name in new[] { "RunLevel", "LogonType", "Arguments", "ExecutionTimeLimit" })
            {
                var changed = XElement.Parse(xml);
                changed.Descendants(Ns + name).Single().Value = "changed";
                Check(!StartupService.TaskAligned(changed.ToString(), exe, sid), "未识别任务漂移：" + name);
            }
            foreach (var name in new[] { "DisallowStartIfOnBatteries", "StopIfGoingOnBatteries", "RunOnlyIfIdle", "RunOnlyIfNetworkAvailable" })
            {
                var changed = XElement.Parse(xml);
                changed.Descendants(Ns + name).Single().Value = "true";
                Check(!StartupService.TaskAligned(changed.ToString(), exe, sid), "未识别常驻条件变化：" + name);
            }
            var foreign = XElement.Parse(xml);
            foreign.Descendants(Ns + "Source").Single().Value = "another-app";
            Check(!StartupService.TaskOwnedBy(foreign.ToString(), sid), "覆盖了其他程序任务");
        });
        step("自启动：Windows 回读账号名与 SID 等价，不误认其他账号", () =>
        {
            var sid = StartupService.CurrentUserSid;
            var account = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            const string exe = @"E:\中文 & 空格\FlashTrans.exe";
            var xml = XElement.Parse(StartupService.BuildTaskXml(exe, sid));
            xml.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!.Element(Ns + "UserId")!.Value = account;
            Check(StartupService.TaskAligned(xml.ToString(), exe, sid), "登录账号被 Windows 转成名称后误报失败");
            xml.Descendants(Ns + "UserId").ToList().ForEach(user => user.Value = account);
            Check(StartupService.TaskAligned(xml.ToString(), exe, sid), "主体账号名与 SID 未按同一身份判断");
            var other = new SecurityIdentifier("S-1-5-18").Translate(typeof(NTAccount)).Value;
            Check(!StartupService.TaskUserMatches(other, sid) && !StartupService.TaskUserMatches(null, sid),
                "其他账号或空账号被误认成当前用户");
        });
        step("自启动：自测和提权助手参数不能修改用户启动项", () =>
        {
            var before = StartupService.CurrentCommand();
            Check(!StartupService.Sync(true, true), "自测进程能写管理员任务");
            Check(StartupService.ChangeAsync(true, true).GetAwaiter().GetResult() is not null, "自测进程触发了授权");
            Check(StartupService.HandleConfigurationCommand([StartupService.ConfigureArgument, "admin", StartupService.CurrentUserSid]) == 2,
                "助手未隔离自测宿主");
            Check(StartupService.HandleConfigurationCommand(["--tray"]) is null, "普通启动被助手接管");
            Check(StartupService.CurrentCommand() == before, "自测更改了用户的普通自启");
        });
    }

    public static void RunUi(Action<string, Action> step, string? preview)
    {
        step("自启动：深浅主题、最小/常用/最大化、开关依赖及失败回退", () =>
        {
            var original = SettingsService.Instance.Current;
            try
            {
                foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
                foreach (var size in new[] { "minimum", "normal", "maximized" })
                {
                    SettingsService.Instance.Apply(new AppSettings { Theme = theme, RunAtStartup = false });
                    ThemeService.ApplyTheme(theme);
                    var window = new SettingsWindow(new AppHost()) { Width = size == "minimum" ? 620 : 720,
                        Height = size == "minimum" ? 440 : 560, ShowInTaskbar = false };
                    try
                    {
                        window.Show();
                        if (size == "maximized") window.WindowState = WindowState.Maximized;
                        Pump(); window.UpdateLayout();
                        var admin = Descendants<CheckBox>(window).Single(b => b.Name == "StartupAsAdmin");
                        Check(!admin.IsEnabled, "关闭自启后管理员选项仍可修改");
                        SettingsService.Instance.Current.RunAtStartup = true;
                        window.SelectTab("general"); Pump(); window.UpdateLayout();
                        admin = Descendants<CheckBox>(window).Single(b => b.Name == "StartupAsAdmin");
                        Check(admin.IsEnabled, "打开自启后无法选择管理员模式");
                        admin.IsChecked = true;
                        admin.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); Pump();
                        Check(!SettingsService.Instance.Current.StartupAsAdmin && admin.IsChecked == false && admin.IsEnabled,
                            "失败后开关没有恢复，或仍在忙碌状态");
                        Check(Descendants<TextBlock>(window).Single(t => t.Name == "StartupStatus").Text.Contains("自测"),
                            "失败没有显示原因");
                        if (preview is not null)
                        {
                            // 截取正常状态，错误状态已由真实点击路径验证。
                            window.SelectTab("general"); Pump(); window.UpdateLayout();
                            Directory.CreateDirectory(preview);
                            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(window);
                            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                            using var stream = File.Create(Path.Combine(preview, $"startup-{theme}-{size}.png"));
                            png.Save(stream);
                        }
                    }
                    finally { window.Close(); }
                }
            }
            finally { SettingsService.Instance.Apply(original); ThemeService.Apply(original); }
        });
    }

    public static void TaskIntegration()
    {
        var name = "FlashTrans.SelfTest.Startup." + Guid.NewGuid().ToString("N");
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", ".tmp", "startup-test"));
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, name + ".txt");
        var exe = Path.Combine(AppContext.BaseDirectory, "FlashTrans.SelfTest.exe");
        var xml = XElement.Parse(StartupService.BuildTaskXml(exe, StartupService.CurrentUserSid));
        // 禁用登录触发器，测试期间只允许手动执行；保留触发器以验证 Windows 的账号归一化。
        xml.Descendants(Ns + "RunLevel").Single().Value = "LeastPrivilege";
        xml.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!.Element(Ns + "Enabled")!.Value = "false";
        xml.Descendants(Ns + "Arguments").Single().Value = $"--startup-task-child \"{output}\"";
        using var store = new StartupTaskStore();
        var registered = false;
        try
        {
            Check(store.Read(name) is null, "随机任务名已存在");
            store.Register(name, xml.ToString()); registered = true;
            var saved = XElement.Parse(store.Read(name)!);
            Check(StartupService.TaskOwnedBy(saved.ToString(), StartupService.CurrentUserSid), "Windows 登记后改变了任务账号或归属");
            var trigger = saved.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!;
            Check(StartupService.TaskUserMatches((string?)trigger.Element(Ns + "UserId"), StartupService.CurrentUserSid),
                "Windows 回读的登录触发器账号没有识别为当前用户");
            // 仅在内存中还原生产参数，验证系统回读后的完整校验；实际任务仍保持非提权且触发器禁用。
            var verify = new XElement(saved);
            verify.Element(Ns + "Principals")!.Element(Ns + "Principal")!.SetElementValue(Ns + "RunLevel", "HighestAvailable");
            verify.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!.Element(Ns + "Enabled")!.Value = "true";
            verify.Descendants(Ns + "Arguments").Single().Value = StartupService.TrayArg;
            Check(StartupService.TaskAligned(verify.ToString(), exe, StartupService.CurrentUserSid),
                "Windows 实际登记并回读后，启动任务校验仍然失败");
            Check(saved.Descendants(Ns + "Arguments").Single().Value == xml.Descendants(Ns + "Arguments").Single().Value,
                "Windows 未保留程序参数");
            xml.Descendants(Ns + "Description").Single().Value = "FlashTrans isolated test";
            store.Register(name, xml.ToString());
            Check(store.Read(name)!.Contains("FlashTrans isolated test"), "任务更新失败");
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
            try
            {
                service.Connect();
                dynamic folder = service.GetFolder(@"\");
                try
                {
                    dynamic task = folder.GetTask(name);
                    try { object running = task.Run(null); Marshal.ReleaseComObject(running); }
                    finally { Marshal.ReleaseComObject(task); }
                }
                finally { Marshal.ReleaseComObject(folder); }
            }
            finally { Marshal.ReleaseComObject(service); }
            var until = Environment.TickCount64 + 15000;
            while (!File.Exists(output) && Environment.TickCount64 < until) Thread.Sleep(50);
            Check(File.Exists(output) && File.ReadAllText(output) == "started", "任务未成功启动隔离子进程");
            store.Delete(name); registered = false;
            Check(store.Read(name) is null, "任务删除后仍存在");
        }
        finally
        {
            if (registered) store.Delete(name);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
