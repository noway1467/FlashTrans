using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

static class QuickLaunchProbe
{
    public static int Child(string[] args)
    {
        if (args[0] == "--quick-launch-child") { File.WriteAllText(args[1], "started"); return 0; }
        var spec = HotkeySpec.Parse(args[1]);
        if (!Win32.RegisterHotKey(IntPtr.Zero, 0x7100, spec.Win32Modifiers, spec.VirtualKey)) return 2;
        try
        {
            File.WriteAllText(args[2], "ready");
            var watch = Stopwatch.StartNew();
            while (!File.Exists(args[2] + ".stop") && watch.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(25);
        }
        finally { Win32.UnregisterHotKey(IntPtr.Zero, 0x7100); }
        return 0;
    }

    public static void RunAll(Action<string, Action> step, string? preview = null)
    {
        step("快速启动：配置往返、升级兼容、非法路径与重复项", Configuration);
        step("快速启动：全局/截图/录制冲突与符号快捷键", Conflicts);
        step("快速启动：独立进程占用检测、旧键保留和真实热键消息", NativeHotkeys);
        step("快速启动：批量换键不受注册顺序影响", RebindTransfers);
        step("快速启动：录入已注册全局键不误触发功能", RegisteredKeyEditing);
        step("快速启动：真实启动快捷方式与程序路径", Launch);
        step("快速启动：设置页改名、排序、移除及快捷键编辑", SettingsUi);
        step("快速启动：深浅主题、搜索、键盘、空态与失败提示", () => WindowUi(preview));
    }

    static void Configuration()
    {
        var old = JsonSerializer.Deserialize("{\"version\":10,\"hkToggleWindow\":\"Ctrl+Alt+Space\"}", SettingsJson.Default.AppSettings)!;
        Check(SettingsService.Migrate(old) && old.Version == AppSettings.CurrentVersion, "旧设置没有迁移");
        Check(old.HkQuickLaunch == "" && old.HkToggleWindow == "Ctrl+Alt+Space", "新增默认键抢了旧功能");
        var defaults = JsonSerializer.Deserialize("{}", SettingsJson.Default.AppSettings)!;
        Check(defaults.HkQuickLaunch == "Ctrl+Alt+Space" && defaults.QuickLaunchItems.Count == 0, "缺省配置错误");
        var path = Path.Combine(Path.GetTempPath(), "QuickLaunch-未挂载", "app.exe");
        defaults.QuickLaunchItems = [new() { Name = " 我的程序 ", Path = path }, new() { Path = path.ToUpperInvariant() },
            new() { Path = "relative.exe" }, new() { Path = "https://example.com/run.exe" }, null!,
            new() { Path = Path.Combine(Path.GetTempPath(), "bad.cmd") }, new() { Name = "目录", IsFolder = true, Path = Path.GetTempPath() }];
        SettingsService.Normalize(defaults);
        Check(defaults.QuickLaunchItems.Count == 2 && defaults.QuickLaunchItems[0].Name == "我的程序", "归一化未去重或丢了离线项目");
        defaults.HkQuickLaunch = "Ctrl+Shift+F8";
        var json = JsonSerializer.Serialize(defaults, SettingsJson.Default.AppSettings);
        var copy = JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings)!;
        SettingsService.Normalize(copy);
        Check(JsonSerializer.Serialize(copy, SettingsJson.Default.AppSettings) == json, "配置往返或重复归一化不稳定");
        Check(copy.QuickLaunchItems[1].IsFolder && copy.HkQuickLaunch == defaults.HkQuickLaunch, "类型/快捷键未保存");
    }

    static void Conflicts()
    {
        var s = AppSettings.CreateDefault();
        Check(HotkeyManager.FindConflict(s, HotkeyAction.QuickLaunch, s.HkQuickLaunch) is null, "默认键冲突");
        foreach (var text in new[] { "Alt+Control+q", "F1", RecordHud.PauseHotkey, RecordHud.CancelHotkey, s.CkAutoRedact, "Ctrl+D" })
            Check(HotkeyManager.FindConflict(s, HotkeyAction.QuickLaunch, text) is not null, "未拦截冲突：" + text);
        Check(HotkeyManager.FindConflict(s, HotkeyAction.ToggleWindow, s.HkQuickLaunch) is not null, "其他全局键可覆盖快速启动");
        foreach (var key in new[] { Key.Oem3, Key.OemMinus, Key.OemPlus, Key.OemComma, Key.OemPeriod, Key.OemQuestion,
            Key.OemSemicolon, Key.OemQuotes, Key.OemOpenBrackets, Key.Oem6, Key.Oem5, Key.Space })
        {
            var spec = new HotkeySpec(ModifierKeys.Control | ModifierKeys.Alt, key);
            Check(HotkeySpec.Parse(spec.ToString()) == spec, "符号键不能往返：" + key);
        }
    }

    static string AvailableKey(bool modifiers = true, string? exclude = null)
    {
        using var message = new MessageWindow("QuickLaunch.Available");
        foreach (var key in Enumerable.Range(2, 10))
        {
            var text = (modifiers ? "Ctrl+Alt+Shift+" : "") + "F" + key;
            if (text == exclude) continue;
            var spec = HotkeySpec.Parse(text);
            if (!Win32.RegisterHotKey(message.Handle, 0x7200, spec.Win32Modifiers, spec.VirtualKey)) continue;
            Win32.UnregisterHotKey(message.Handle, 0x7200);
            return text;
        }
        throw new InvalidOperationException("没有可用于测试的空闲组合键");
    }

    static Process StartChild(params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }

    static void NativeHotkeys() => InTemp(dir =>
    {
        var first = AvailableKey();
        var occupied = AvailableKey(exclude: first);
        var signal = Path.Combine(dir, "ready");
        using var child = StartChild("--quick-launch-hotkey-child", occupied, signal);
        var inputTarget = new Window { Title = "FlashTrans 热键自测", Width = 260, Height = 90, ShowInTaskbar = false };
        try
        {
            Wait(() => File.Exists(signal), "占用快捷键的子进程没有就绪");
            inputTarget.Show(); inputTarget.Activate(); Pump();
            using var message = new MessageWindow("QuickLaunch.NativeTest");
            using var manager = new HotkeyManager(message.Handle);
            message.Message += (msg, wp, _) => msg == Win32.WM_HOTKEY && manager.Handle(wp);
            var count = 0;
            var host = new AppHost();
            manager.Triggered += action => { if (action == HotkeyAction.QuickLaunch) { count++; host.ToggleQuickLaunch(); } };
            var s = AppSettings.CreateDefault();
            Check(manager.TryChange(s, HotkeyAction.QuickLaunch, first, out var error), error ?? "首次注册失败");
            Check(!manager.TryChange(s, HotkeyAction.QuickLaunch, occupied, out error) && error!.Contains("占用"), "未检测到独立进程占用");
            SendHotkey(first);
            Wait(() => count == 1, "改键失败后旧快捷键失效");
            var window = Application.Current.Windows.OfType<QuickLaunchWindow>().Single();
            Check(window.IsVisible, "真实快捷键没有呼出面板");
            SendHotkey(first);
            Wait(() => count == 2, "第二次热键消息未收到");
            Check(!window.IsVisible, "第二次快捷键未隐藏");
            window.Close();
            Check(manager.TryChange(s, HotkeyAction.QuickLaunch, "", out _), "清除失败");
            var spec = HotkeySpec.Parse(first);
            using var released = new MessageWindow("QuickLaunch.Released");
            Check(Win32.RegisterHotKey(released.Handle, 0x7300, spec.Win32Modifiers, spec.VirtualKey), "清除后仍占键");
            Win32.UnregisterHotKey(released.Handle, 0x7300);
        }
        finally
        {
            inputTarget.Close();
            File.WriteAllText(signal + ".stop", "stop");
            if (!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(); }
            foreach (var w in Application.Current.Windows.OfType<QuickLaunchWindow>().ToArray()) w.Close();
        }
    });

    static void SendHotkey(string text)
    {
        var spec = HotkeySpec.Parse(text);
        ushort[] keys = [0x11, 0x12, 0x10, (ushort)spec.VirtualKey];
        var inputs = keys.Select(k => new INPUT { type = Win32.INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = k } } })
            .Concat(keys.Reverse().Select(k => new INPUT { type = Win32.INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wVk = k, dwFlags = Win32.KEYEVENTF_KEYUP } } })).ToArray();
        Check(Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length, "SendInput 未完整发送");
        Pump();
    }

    static AppSettings EmptyHotkeys()
    {
        var s = AppSettings.CreateDefault();
        foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.Name.StartsWith("Hk")))
            property.SetValue(s, "");
        return s;
    }

    static void RebindTransfers()
    {
        var first = AvailableKey();
        var second = AvailableKey(exclude: first);
        var s = EmptyHotkeys();
        s.HkQuickLaunch = first;
        s.HkToggleWindow = second;
        using var message = new MessageWindow("QuickLaunch.Transfer");
        using var manager = new HotkeyManager(message.Handle);
        message.Message += (msg, wp, _) => msg == Win32.WM_HOTKEY && manager.Handle(wp);
        var fired = new List<HotkeyAction>();
        manager.Triggered += fired.Add;
        manager.Rebind(s);
        Check(manager.Failures.Count == 0, "初次绑定失败");
        (s.HkQuickLaunch, s.HkToggleWindow) = (s.HkToggleWindow, s.HkQuickLaunch);
        manager.Rebind(s);
        Check(manager.Failures.Count == 0, "交换键位后部分功能失效：" + string.Join("；", manager.Failures));
        manager.Rebind(s);
        SendHotkey(first);
        SendHotkey(second);
        Wait(() => fired.Count == 2, "换键/重复保存后热键没有完整触发");
        Check(fired.SequenceEqual(new[] { HotkeyAction.ToggleWindow, HotkeyAction.QuickLaunch }), "热键仍指向旧功能");
    }

    static void RegisteredKeyEditing() => WithSettings(() =>
    {
        var s = EmptyHotkeys();
        s.HkQuickLaunch = AvailableKey();
        s.HkToggleWindow = AvailableKey(exclude: s.HkQuickLaunch);
        SettingsService.Instance.Apply(s);
        using var message = new MessageWindow("QuickLaunch.Editing");
        using var manager = new HotkeyManager(message.Handle);
        var host = new AppHost();
        // 单独验证宿主分发和真实设置窗口，避免启动鼠标钩子、后台预热或托盘。
        // SendInput / WM_HOTKEY 的端到端通路由 NativeHotkeys 覆盖。
        typeof(AppHost).GetField("_hotkeys", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, manager);
        var dispatch = typeof(AppHost).GetMethod("OnHotkey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        manager.Triggered += action => dispatch.Invoke(host, [action]);
        message.Message += (msg, wp, _) => msg == Win32.WM_HOTKEY && manager.Handle(wp);
        manager.Rebind(s);
        host.ShowSettings("quicklaunch");
        var window = Application.Current.Windows.OfType<SettingsWindow>().Single();
        try
        {
            window.UpdateLayout(); Pump();
            var box = Descendants<TextBox>(window).Single(b => b.Name == "HotkeyQuickLaunch");
            box.Focus(); Keyboard.Focus(box); Pump();
            Check(box.IsKeyboardFocused, "快捷键输入框没有获得焦点");
            dispatch.Invoke(host, [HotkeyAction.QuickLaunch]);
            Pump();
            Check(!Application.Current.Windows.OfType<QuickLaunchWindow>().Any(w => w.IsVisible), "录入自己的快捷键却打开了面板");
            Check(window.IsActive && box.Text == s.HkQuickLaunch, "录入原键丢了焦点或键值");
            dispatch.Invoke(host, [HotkeyAction.ToggleWindow]);
            Pump();
            Check(!Application.Current.Windows.OfType<MainWindow>().Any(w => w.IsVisible)
                && box.Text == s.HkQuickLaunch, "录入冲突键误触发原功能或覆盖设置");
        }
        finally
        {
            window.Close();
            foreach (var w in Application.Current.Windows.OfType<QuickLaunchWindow>().ToArray()) w.Close();
        }
    });

    static void Launch() => InTemp(dir =>
    {
        var marker = Path.Combine(dir, "launched");
        var link = Path.Combine(dir, "中文 空格 & 快捷方式.lnk");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = shell.CreateShortcut(link);
        try
        {
            shortcut.TargetPath = Environment.ProcessPath!;
            shortcut.Arguments = $"\"{Assembly.GetExecutingAssembly().Location}\" --quick-launch-child \"{marker}\"";
            shortcut.WindowStyle = 7;
            shortcut.Save();
        }
        finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        var s = AppSettings.CreateDefault();
        Check(QuickLaunchService.TryAdd(s, link, false, out _), "快捷方式添加失败");
        Check(!QuickLaunchService.TryAdd(s, link, false, out _), "重复添加未阻止");
        QuickLaunchService.Launch(s.QuickLaunchItems[0]);
        Wait(() => File.Exists(marker), "真实快捷方式未启动隔离子进程");
        Check(QuickLaunchService.TryAdd(s, dir, true, out _), "文件夹添加失败");
        var folderInfo = QuickLaunchService.CreateStartInfo(s.QuickLaunchItems[1]);
        Check(folderInfo.FileName == dir && folderInfo.UseShellExecute && folderInfo.Arguments == "", "文件夹被当作命令行处理");
        Check(QuickLaunchService.TryAdd(s, Environment.ProcessPath!, false, out _), "exe 添加失败");
        var exeInfo = QuickLaunchService.CreateStartInfo(s.QuickLaunchItems[2]);
        Check(exeInfo.WorkingDirectory == Path.GetDirectoryName(Environment.ProcessPath), "exe 工作目录错误");
        exeInfo.WindowStyle = ProcessWindowStyle.Hidden;
        using var exe = Process.Start(exeInfo);
        Check(exe is not null && exe.WaitForExit(10000), "exe 没有成功启动并退出");
    });

    static void SettingsUi() => WithSettings(() => InTemp(dir =>
    {
        var s = SettingsService.Instance.Current;
        var second = Directory.CreateDirectory(Path.Combine(dir, "第二项")).FullName;
        QuickLaunchService.TryAdd(s, dir, true, out _);
        QuickLaunchService.TryAdd(s, second, true, out _);
        var window = new SettingsWindow(new AppHost());
        try
        {
            window.SelectTab("quicklaunch"); window.Show(); window.UpdateLayout(); Pump();
            var name = Descendants<TextBox>(window).First(b => b.Name == "QuickLaunchName");
            name.Text = "常用目录";
            name.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Check(s.QuickLaunchItems[0].Name == "常用目录", "显示名称没保存");
            Click(Descendants<Button>(window).First(b => Equals(b.ToolTip, "下移")));
            Check(s.QuickLaunchItems[1].Name == "常用目录", "排序失败");
            Click(Descendants<Button>(window).First(b => Equals(b.ToolTip, "移除（不删除文件）")));
            Check(s.QuickLaunchItems.Count == 1 && Directory.Exists(second), "移除误删了文件夹或列表没更新");
            var keyBox = Descendants<TextBox>(window).Single(b => b.Name == "HotkeyQuickLaunch");
            var old = s.HkQuickLaunch;
            PressKey(window, keyBox, Key.F1);
            Check(s.HkQuickLaunch == old && keyBox.Text == old, "重复快捷键覆盖了原设置");
            var available = AvailableKey(modifiers: false);
            PressKey(window, keyBox, HotkeySpec.Parse(available).Key);
            Check(s.HkQuickLaunch == available, "自定义快捷键未保存");
            window.SelectTab("hotkeys"); window.UpdateLayout();
            keyBox = Descendants<TextBox>(window).Single(b => b.Name == "HotkeyQuickLaunch");
            Check(keyBox.Text == available, "两个设置入口未同步");
            PressKey(window, keyBox, Key.Back);
            Check(s.HkQuickLaunch == "", "快捷键清除失败");
            SettingsService.Instance.Load();
            Check(SettingsService.Instance.Current.QuickLaunchItems.Single().Name == "常用目录"
                && SettingsService.Instance.Current.HkQuickLaunch == "", "磁盘配置重新加载不一致");
        }
        finally { window.Close(); }
    }));

    static void WindowUi(string? preview) => WithSettings(() => InTemp(dir =>
    {
        var s = SettingsService.Instance.Current;
        s.QuickLaunchItems = [new() { Name = "工作资料", Path = dir, IsFolder = true },
            new() { Name = "Visual Studio Code", Path = Path.Combine(dir, "Code.exe") },
            new() { Name = "项目文件夹", Path = Path.Combine(dir, "Projects"), IsFolder = true }];
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            ThemeService.ApplyTheme(theme);
            var managed = false;
            var w = new QuickLaunchWindow(() => managed = true);
            try
            {
                w.ShowPanel(); w.UpdateLayout(); Pump();
                var search = Descendants<TextBox>(w).Single();
                var list = Descendants<ListBox>(w).Single();
                Check(w.IsVisible && search.IsKeyboardFocused && list.Items.Count == 3, "呼出没有焦点或列表");
                PressKey(w, search, Key.Down); Check(list.SelectedIndex == 1, "下键没选择下一项");
                PressKey(w, search, Key.Up); Check(list.SelectedIndex == 0, "上键没选择上一项");
                if (preview is not null) Snapshot(w, Path.Combine(preview, $"quicklaunch-{theme}.png"));
                search.Text = "VISUAL code"; Check(list.Items.Count == 1, "大小写/多词过滤失败");
                PressKey(w, search, Key.Enter);
                Check(w.IsVisible && Descendants<TextBlock>(w).Any(t => t.Text.StartsWith("启动失败")), "失效项目无提示或错误关闭");
                search.Text = "无匹配项"; Check(list.Items.Count == 0, "过滤未清空列表");
                PressKey(w, search, Key.Down); PressKey(w, search, Key.Enter);
                Check(w.IsVisible, "空列表误启动");
                PressKey(w, search, Key.Escape); Check(!w.IsVisible, "Esc 没关闭");
                w.ShowPanel(); Check(search.Text == "" && list.Items.Count == 3, "重复呼出没有重置搜索");
                Click(Descendants<Button>(w).Single(b => Equals(b.Content, "管理列表")));
                Check(managed && !w.IsVisible, "管理入口没有切换");
            }
            finally { w.Close(); }
        }
        s.QuickLaunchItems.Clear();
        var empty = new QuickLaunchWindow(() => { });
        try
        {
            empty.ShowPanel(); empty.UpdateLayout();
            Check(Descendants<TextBlock>(empty).Any(t => t.Text.Contains("还没有启动项目")), "空态没有添加指引");
        }
        finally { empty.Close(); }
    }));

    static void WithSettings(Action action)
    {
        var original = SettingsService.Instance.Current;
        try { SettingsService.Instance.Apply(AppSettings.CreateDefault()); action(); }
        finally { SettingsService.Instance.Apply(original); ThemeService.Apply(original); }
    }

    static void InTemp(Action<string> action)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FlashTrans-QuickLaunch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { action(dir); }
        finally
        {
            var full = Path.GetFullPath(dir);
            Check(full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("FlashTrans-QuickLaunch-"), "临时目录安全检查失败");
            Directory.Delete(full, recursive: true);
        }
    }

    static void Snapshot(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }

    static void PressKey(Window window, UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(window), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); }
    static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    static void Wait(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(20); }
        Check(condition(), message);
    }
    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
