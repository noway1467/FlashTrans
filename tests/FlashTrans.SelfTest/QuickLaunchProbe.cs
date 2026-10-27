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
        step("快速启动：相关度、首字母缩写与使用次数排序", Ranking);
        step("快速启动：全局/截图/录制冲突与符号快捷键", Conflicts);
        step("快速启动：独立进程占用检测、旧键保留和真实热键消息", NativeHotkeys);
        step("快速启动：批量换键不受注册顺序影响", RebindTransfers);
        step("快速启动：录入已注册全局键不误触发功能", RegisteredKeyEditing);
        step("快速启动：真实启动快捷方式与程序路径", Launch);
        step("快速启动：设置页改名、排序、移除、拖放添加及快捷键编辑", () => SettingsUi(preview));
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
        defaults.QuickLaunchItems = [new() { Name = " 我的程序 ", Path = path, LaunchCount = 7, LastLaunched = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero) },
            new() { Path = path.ToUpperInvariant() },
            new() { Path = "relative.exe" }, new() { Path = "https://example.com/run.exe" }, null!,
            new() { Path = Path.Combine(Path.GetTempPath(), "bad.cmd") }, new() { Path = Path.Combine(Path.GetTempPath(), "run.PS1") },
            new() { Name = "目录", IsFolder = true, Path = Path.GetTempPath() },
            new() { Path = Path.Combine(Path.GetTempPath(), "报告.docx"), LaunchCount = -3 }];
        SettingsService.Normalize(defaults);
        Check(defaults.QuickLaunchItems.Count == 3 && defaults.QuickLaunchItems[0].Name == "我的程序", "归一化未去重、丢了离线项目或放过了脚本");
        Check(defaults.QuickLaunchItems[0].LaunchCount == 7 && defaults.QuickLaunchItems[0].LastLaunched?.Day == 1
            && defaults.QuickLaunchItems[2].LaunchCount == 0, "使用记录没有保留或负数没有归零");
        Check(QuickLaunchService.KindOf(defaults.QuickLaunchItems[2]) == QuickLaunchKind.File
            && !QuickLaunchService.CanRunAsAdmin(defaults.QuickLaunchItems[2]) && QuickLaunchService.CanRunAsAdmin(defaults.QuickLaunchItems[0]), "类型判断错误");
        defaults.HkQuickLaunch = "Ctrl+Shift+F8";
        var json = JsonSerializer.Serialize(defaults, SettingsJson.Default.AppSettings);
        var copy = JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings)!;
        SettingsService.Normalize(copy);
        Check(JsonSerializer.Serialize(copy, SettingsJson.Default.AppSettings) == json, "配置往返或重复归一化不稳定");
        Check(copy.QuickLaunchItems[1].IsFolder && copy.HkQuickLaunch == defaults.HkQuickLaunch, "类型/快捷键未保存");
    }

    static void Ranking()
    {
        QuickLaunchEntry E(string name, int count = 0) => new() { Name = name, Path = @"D:\Apps\" + name.Replace(" ", "") + ".exe", LaunchCount = count };
        var items = new List<QuickLaunchEntry> { E("Notepad++"), E("Visual Studio Code"), E("Microsoft Visual Studio", 3),
            E("OneNote"), E("Code Runner"), E("微信", 9), E("企业微信") };
        string[] Names(string query) => QuickLaunchService.Search(items, query).Select(i => i.Name).ToArray();
        Check(Names("").SequenceEqual(items.Select(i => i.Name)), "空搜索打乱了手动顺序");
        Check(Names("vsc").FirstOrDefault() == "Visual Studio Code", "首字母缩写未命中");
        Check(Names("code").SequenceEqual(new[] { "Code Runner", "Visual Studio Code" }), "前缀匹配应排在词中匹配前");
        Check(Names("visual").SequenceEqual(new[] { "Visual Studio Code", "Microsoft Visual Studio" }), "名称前缀应优先于使用次数");
        Check(Names("studio").SequenceEqual(new[] { "Microsoft Visual Studio", "Visual Studio Code" }), "同分时没有按使用次数排序");
        Check(Names("on").FirstOrDefault() == "OneNote" && Names("note").Contains("Notepad++") && Names("note").Contains("OneNote"), "驼峰词首没有识别");
        Check(Names("微信").SequenceEqual(new[] { "微信", "企业微信" }), "中文全名应排在包含匹配前");
        Check(Names("apps notepad").SingleOrDefault() == "Notepad++" && Names("zzz").Length == 0, "多词或路径过滤失败");
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
        Check(QuickLaunchService.Launch(s.QuickLaunchItems[0]), "启动返回了取消");
        Wait(() => File.Exists(marker), "真实快捷方式未启动隔离子进程");
        Check(s.QuickLaunchItems[0].LaunchCount == 1 && s.QuickLaunchItems[0].LastLaunched is not null, "启动次数没有记录");
        Check(QuickLaunchService.TryAdd(s, dir, true, out _), "文件夹添加失败");
        var folderInfo = QuickLaunchService.CreateStartInfo(s.QuickLaunchItems[1]);
        Check(folderInfo.FileName == dir && folderInfo.UseShellExecute && folderInfo.Arguments == "", "文件夹被当作命令行处理");
        Throws<InvalidOperationException>(() => QuickLaunchService.CreateStartInfo(s.QuickLaunchItems[1], asAdmin: true));
        Check(QuickLaunchService.TryAdd(s, Environment.ProcessPath!, false, out _), "exe 添加失败");
        var exeInfo = QuickLaunchService.CreateStartInfo(s.QuickLaunchItems[2]);
        Check(exeInfo.WorkingDirectory == Path.GetDirectoryName(Environment.ProcessPath), "exe 工作目录错误");
        Check(QuickLaunchService.CreateStartInfo(s.QuickLaunchItems[2], asAdmin: true).Verb == "runas" && exeInfo.Verb == "", "管理员运行参数错误");
        var document = Path.Combine(dir, "会议 纪要.txt");
        var script = Path.Combine(dir, "run.cmd");
        File.WriteAllText(document, "notes");
        File.WriteAllText(script, "@echo off");
        var (added, error) = QuickLaunchService.TryAddPaths(s, [document, script, Path.Combine(dir, "missing.pdf")]);
        Check(added == 1 && error is not null && !s.QuickLaunchItems.Any(i => i.Path == script), "拖放添加没有拦截脚本或缺失文件");
        var docInfo = QuickLaunchService.CreateStartInfo(s.QuickLaunchItems[3]);
        var gone = new QuickLaunchEntry { Name = "已删除", Path = Path.Combine(dir, "gone.exe") };
        var goneFolder = new QuickLaunchEntry { Name = "已删目录", Path = Path.Combine(dir, "gone"), IsFolder = true };
        Check(QuickLaunchService.FindMissing([.. s.QuickLaunchItems, gone, goneFolder]).SequenceEqual(new[] { gone, goneFolder }), "失效项查找错误");
        Check(docInfo.FileName == document && docInfo.UseShellExecute && docInfo.WorkingDirectory == "" && !s.QuickLaunchItems[3].IsFolder, "文档没有交给关联程序打开");
        var drive = Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\").FirstOrDefault(d => !Directory.Exists(d));
        if (drive is not null)
            Throws<IOException>(() => QuickLaunchService.OpenLocation(new QuickLaunchEntry { Path = Path.Combine(drive, "gone", "app.exe") }));
        exeInfo.WindowStyle = ProcessWindowStyle.Hidden;
        using var exe = Process.Start(exeInfo);
        Check(exe is not null && exe.WaitForExit(10000), "exe 没有成功启动并退出");
    });

    static void SettingsUi(string? preview) => WithSettings(() => InTemp(dir =>
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
            s = SettingsService.Instance.Current;
            window.Close();
            window = new SettingsWindow(new AppHost());
            window.SelectTab("quicklaunch"); window.Show(); window.UpdateLayout(); Pump();
            var file = Path.Combine(dir, "清单.xlsx");
            File.WriteAllText(file, "");
            window.AddQuickLaunchPaths([file, Path.Combine(dir, "setup.msi")]);
            Check(s.QuickLaunchItems.Count == 2 && s.QuickLaunchItems[1].Path == file && !s.QuickLaunchItems[1].IsFolder, "设置页拖放没有添加文件");
            window.UpdateLayout();
            Check(Descendants<TextBlock>(window).Any(t => Plain(t).StartsWith("文件 · ")), "设置页没有显示项目类型");
            Check(Descendants<Button>(window).Any(b => b.Content is StackPanel p && p.Children.OfType<TextBlock>().Any(t => t.Text == "添加文件")), "缺少添加文件入口");
            Check(Descendants<Button>(window).Any(b => Equals(b.Content, "清理失效项")), "缺少清理失效项入口");
            if (preview is not null)
            {
                Wait(() => Descendants<Image>(window).Count() >= 2, "设置页没有取到系统图标");
                window.UpdateLayout();
                Snapshot(window, Path.Combine(preview, "quicklaunch-settings.png"));
            }
        }
        finally { window.Close(); }
    }));

    static void WindowUi(string? preview) => WithSettings(() => InTemp(dir =>
    {
        var s = SettingsService.Instance.Current;
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var notes = Path.Combine(dir, "周报 2026.txt");
        File.WriteAllText(notes, "notes");
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            s.QuickLaunchItems = [new() { Name = "工作资料", Path = dir, IsFolder = true },
                new() { Name = "Visual Studio Code", Path = Path.Combine(dir, "Code.exe") },
                new() { Name = "记事本", Path = notepad, LaunchCount = 4 },
                new() { Name = "周报 2026", Path = notes },
                new() { Name = "项目文件夹", Path = Path.Combine(dir, "Projects"), IsFolder = true }];
            ThemeService.ApplyTheme(theme);
            var managed = false;
            var w = new QuickLaunchWindow(() => managed = true);
            // 面板失去激活就会自动隐藏（设计如此）。记下当时前台是谁，失败时能分清是程序问题还是有人在操作桌面。
            var lostTo = new List<string>();
            w.Deactivated += (_, _) =>
            {
                Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out var pid);
                try { lostTo.Add(pid == Environment.ProcessId ? "自测进程自身" : Process.GetProcessById((int)pid).ProcessName); }
                catch (ArgumentException) { lostTo.Add("pid " + pid); }
            };
            try
            {
                try
                {
                w.ShowPanel(); w.UpdateLayout(); Pump();
                var search = Descendants<TextBox>(w).Single();
                var list = Descendants<ListBox>(w).Single();
                Check(w.IsVisible && search.IsKeyboardFocused && list.Items.Count == 5,
                    $"呼出没有焦点或列表：可见 {w.IsVisible}，激活 {w.IsActive}，搜索框焦点 {search.IsKeyboardFocused}，"
                    + $"当前焦点 {Keyboard.FocusedElement?.GetType().Name ?? "无"}，列表 {list.Items.Count} 项");
                PressKey(w, search, Key.Down); Check(list.SelectedIndex == 1, "下键没选择下一项");
                PressKey(w, search, Key.Up); Check(list.SelectedIndex == 0, "上键没选择上一项");
                // 图标和存在性在后台线程判断，等它们回到界面再截图。
                Wait(() => Descendants<Image>(w).Count() >= 3, "没有取到系统图标");
                Wait(() => Descendants<TextBlock>(w).Count(t => Plain(t).StartsWith("找不到目标")) == 2, "失效项目没有标出");
                Check(File.Exists(notepad), "测试环境缺少系统记事本");
                if (preview is not null) Snapshot(w, Path.Combine(preview, $"quicklaunch-{theme}.png"));

                var folderMenu = ((ListBoxItem)list.Items[0]).ContextMenu!;
                var exeMenu = ((ListBoxItem)list.Items[2]).ContextMenu!;
                string[] Headers(ContextMenu m) => m.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToArray();
                Check(!Headers(folderMenu).Contains("以管理员身份运行") && Headers(exeMenu).Contains("以管理员身份运行")
                    && Headers(exeMenu).Contains("打开所在位置") && Headers(exeMenu).Contains("复制路径"), "右键菜单项不对");
                exeMenu.PlacementTarget = (ListBoxItem)list.Items[2];
                exeMenu.IsOpen = true; Pump();
                Check(w.IsVisible, "打开右键菜单时面板被隐藏");
                exeMenu.IsOpen = false; Pump();
                var remove = ((ListBoxItem)list.Items[4]).ContextMenu!.Items.OfType<MenuItem>().Single(i => (string)i.Header == "从列表移除");
                remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Check(list.Items.Count == 4 && s.QuickLaunchItems.Count == 4 && Descendants<TextBlock>(w).Any(t => t.Text.StartsWith("已从列表移除")),
                    "右键移除没有更新列表或提示");

                search.Text = "VISUAL code"; Check(list.Items.Count == 1, "大小写/多词过滤失败");
                PressKey(w, search, Key.Enter);
                Check(w.IsVisible && Descendants<TextBlock>(w).Any(t => t.Text.StartsWith("启动失败")), "失效项目无提示或错误关闭");
                search.Text = "vsc"; Check(list.Items.Count == 1, "首字母缩写没命中");
                search.Text = "";
                list.SelectedIndex = 0;
                PressSystemKey(w, search, Key.D2);
                Check(list.SelectedIndex == 1 && w.IsVisible && Descendants<TextBlock>(w).Any(t => t.Text.StartsWith("启动失败")), "Alt+2 没有直达第二项");
                PressSystemKey(w, search, Key.D9);
                Check(list.SelectedIndex == 1, "超出范围的 Alt+数字改变了选择");
                list.Focus(); Pump();
                search.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, list, "记"))
                    { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
                Check(search.Text == "记" && list.Items.Count == 1, "列表焦点下输入没有回到搜索框");
                search.Text = "无匹配项"; Check(list.Items.Count == 0, "过滤未清空列表");
                PressKey(w, search, Key.Down); PressKey(w, search, Key.Enter);
                Check(w.IsVisible, "空列表误启动");
                PressKey(w, search, Key.Escape); Check(!w.IsVisible, "Esc 没关闭");
                w.ShowPanel(); Check(search.Text == "" && list.Items.Count == 4, "重复呼出没有重置搜索");
                Click(Descendants<Button>(w).Single(b => Equals(b.Content, "管理列表")));
                Check(managed && !w.IsVisible, "管理入口没有切换");
                }
                catch (InvalidOperationException ex) when (lostTo.Count > 0)
                {
                    throw new InvalidOperationException($"{ex.Message}（期间面板失去激活 {lostTo.Count} 次，前台依次为：{string.Join("、", lostTo)}）", ex);
                }
            }
            finally { w.Close(); }
        }
        s.QuickLaunchItems.Clear();
        var empty = new QuickLaunchWindow(() => { });
        try
        {
            empty.ShowPanel(); empty.UpdateLayout();
            Check(Descendants<TextBlock>(empty).Any(t => t.Text.Contains("还没有启动项目")), "空态没有添加指引");
            if (preview is not null) Snapshot(empty, Path.Combine(preview, "quicklaunch-empty.png"));
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

    // 窗口是透明的，投影画在外边距里；先铺一层 BgAlt 再贴窗口内容，截图里才看得出投影和圆角。
    static void Snapshot(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        content.Render(window);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.Resources["BgAlt"], null, new Rect(0, 0, content.Width, content.Height));
            dc.DrawImage(content, new Rect(0, 0, content.Width, content.Height));
        }
        var bitmap = new RenderTargetBitmap(content.PixelWidth, content.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }

    static string Plain(TextBlock block) => block.Inlines.Count == 0 ? block.Text
        : string.Concat(block.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
    static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("应抛出 " + typeof(T).Name);
    }
    static void PressSystemKey(Window window, UIElement target, Key key)
    {
        // 按住 Alt 时 WPF 报的是 Key.System，真正的键放在 SystemKey 里；构造同样的事件，不去真按 Alt（松开会进系统菜单模式）。
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        var mark = typeof(KeyEventArgs).GetMethod("MarkSystem", BindingFlags.Instance | BindingFlags.NonPublic);
        if (mark is not null) mark.Invoke(args, null);
        else typeof(KeyEventArgs).GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(args, Key.System);
        Check(args.Key == Key.System && args.SystemKey == key, "无法构造 Alt 组合键事件");
        target.RaiseEvent(args);
        Pump();
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
