using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

static class DesktopShelfProbe
{
    static DesktopShelfSettings S => SettingsService.Instance.Current.DesktopShelf;

    public static void RunAll(Action<string, Action> step, string? preview = null)
    {
        var original = SettingsService.Instance.Current;
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            SettingsService.Instance.Apply(AppSettings.CreateDefault());
            S.AutoHide = false;
            step("桌面收纳：旧配置默认关闭、非法值归一化与配置往返", Configuration);
            step("桌面收纳：七种排序、显示顺序交换与离线项目保留", Ordering);
            step("桌面收纳：四边吸附、负坐标屏幕及脱离显示器恢复", Placement);
            step("桌面收纳：真实移动文件/文件夹、去重与还原", Adding);
            step("桌面收纳：重启恢复、旧入口转换、同名保护与路径边界", StorageRecovery);
            step("桌面收纳：三种配色、最小/常用/大尺寸、空态及长名称", () => Layout(preview));
            step("桌面收纳：WPF 拖放添加、换位、拒绝外来换位与解除", DropAndRemove);
            step("桌面收纳：真实启动快捷方式、UI 响应与失败提示", Launch);
            step("桌面收纳：独立设置页、外观保存与重新载入", () => SettingsUi(preview));
            step("桌面收纳：设置页真实移入和解除还原", SettingsMoves);
            step("桌面收纳：四边收起/展开、菜单与拖放保护、位置不丢失", () => AutoHide(preview));
            step("桌面收纳：关闭自动贴边后真实拖动到四边、鼠标移出隐藏及移入展开", () => ManualEdgeAutoHide(preview));
            step("桌面收纳：默认不创建、启用延后显示、关闭后释放窗口", HostLifecycle);
        }
        finally
        {
            foreach (var window in Application.Current.Windows.OfType<DesktopShelfWindow>().ToArray()) window.Close();
            SettingsService.Instance.Apply(original);
            ThemeService.Apply(original);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    static void Configuration()
    {
        foreach (var defaults in new[] { AppSettings.CreateDefault(), JsonSerializer.Deserialize("{}", SettingsJson.Default.AppSettings)! })
            Check(!defaults.DesktopShelf.Enabled && !defaults.QuickLaunchEnabled && defaults.DesktopShelf.Theme == ShelfTheme.Dark,
                "收纳/快速启动应默认关闭，收纳背景应默认深色");
        var old = JsonSerializer.Deserialize("{\"version\":11}", SettingsJson.Default.AppSettings)!;
        Check(SettingsService.Migrate(old) && old.Version == AppSettings.CurrentVersion && !old.DesktopShelf.Enabled
            && !old.QuickLaunchEnabled && old.DesktopShelf.Theme == ShelfTheme.Dark, "旧配置缺省开关或背景不正确");
        old.DesktopShelf.Items = [new() { Name = " 离线文件 ", Path = Path.Combine(Path.GetTempPath(), "offline.txt") },
            new() { Path = "relative.txt" }, null!];
        old.DesktopShelf.Width = double.PositiveInfinity;
        old.DesktopShelf.Height = -9;
        old.DesktopShelf.BackgroundOpacity = double.NaN;
        old.DesktopShelf.CornerRadius = 1000;
        old.DesktopShelf.IconSize = -1;
        old.DesktopShelf.Theme = (ShelfTheme)500;
        old.DesktopShelf.Sort = (ShelfSort)500;
        old.DesktopShelf.Edge = (ShelfEdge)500;
        SettingsService.Normalize(old);
        var s = old.DesktopShelf;
        Check(s.Width == 280 && s.Height == 160 && s.BackgroundOpacity == 0.92 && s.CornerRadius == 24 && s.IconSize == 24, "尺寸/透明度未归一化");
        Check(s.Theme == ShelfTheme.Dark && s.Sort == ShelfSort.Manual && s.Edge == ShelfEdge.Right, "非法枚举未修复");
        Check(s.Items.Count == 1 && s.Items[0].Name == "离线文件", "离线文件丢失或接受了非法路径");
        s.Enabled = true; s.Left = -600; s.Top = 80; s.Sort = ShelfSort.MostUsed; s.Theme = ShelfTheme.Sky;
        old.QuickLaunchEnabled = true;
        var json = JsonSerializer.Serialize(old, SettingsJson.Default.AppSettings);
        var copy = JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings)!;
        SettingsService.Normalize(copy);
        Check(JsonSerializer.Serialize(copy, SettingsJson.Default.AppSettings) == json, "配置往返不稳定");
        Check(copy.QuickLaunchItems.Count == 0 && copy.DesktopShelf.Enabled, "收纳配置影响了快速启动");
        Check(copy.QuickLaunchEnabled && copy.DesktopShelf.Theme == ShelfTheme.Sky, "新的默认值覆盖了已保存的开关或配色");
        var missing = JsonSerializer.Deserialize("{\"desktopShelf\":null}", SettingsJson.Default.AppSettings)!;
        SettingsService.Normalize(missing);
        Check(!missing.DesktopShelf.Enabled && !missing.QuickLaunchEnabled && missing.DesktopShelf.Theme == ShelfTheme.Dark
            && missing.DesktopShelf.Items.Count == 0, "空配置未修复");
    }

    static void Ordering()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new DesktopShelfEntry { Name = "Alpha", Path = @"D:\Alpha.txt", AddedAt = now.AddDays(-1), LaunchCount = 4, LastLaunched = now };
        var b = new DesktopShelfEntry { Name = "Beta", Path = @"D:\Beta.exe", AddedAt = now, LaunchCount = 7, LastLaunched = now.AddDays(-1) };
        var c = new DesktopShelfEntry { Name = "Gamma", Path = @"D:\Gamma", IsFolder = true };
        var s = new DesktopShelfSettings { Items = [c, b, a] };
        foreach (var (sort, expected) in new[]
        {
            (ShelfSort.Manual, new[] { c, b, a }), (ShelfSort.NameAscending, new[] { a, b, c }),
            (ShelfSort.NameDescending, new[] { c, b, a }), (ShelfSort.Type, new[] { c, b, a }),
            (ShelfSort.AddedNewest, new[] { b, a, c }), (ShelfSort.MostUsed, new[] { b, a, c }),
            (ShelfSort.RecentlyUsed, new[] { a, b, c }),
        })
        {
            s.Sort = sort;
            Check(DesktopShelfService.Ordered(s).SequenceEqual(expected), "排序错误：" + sort);
            Check(s.Items.SequenceEqual(new[] { c, b, a }), "自动排序破坏了手动顺序");
        }
        s.Sort = ShelfSort.NameAscending;
        Check(DesktopShelfService.Swap(s, a.Path, c.Path) && s.Sort == ShelfSort.Manual
            && s.Items.SequenceEqual(new[] { c, b, a }), "未按显示位置交换或未切回手动");
        Check(!DesktopShelfService.Swap(s, a.Path, a.Path) && !DesktopShelfService.Swap(s, a.Path, "missing"), "无效交换改动了数据");
    }

    static void Placement()
    {
        foreach (var scale in new[] { 1d, 1.5, 2 })
        {
            var work = new Rect(-1920 / scale, 40 / scale, 1920 / scale, 1040 / scale);
            foreach (var edge in new[] { ShelfEdge.Left, ShelfEdge.Right, ShelfEdge.Top, ShelfEdge.Bottom })
            {
                var result = DesktopShelfService.Place(new Rect(-800, 180, 240, 300), work, true, edge);
                var box = new Rect(result.Position, new Size(240, 300));
                Check(work.Contains(box), "高 DPI / 负坐标下出界");
                Check(edge switch { ShelfEdge.Left => box.Left == work.Left, ShelfEdge.Right => box.Right == work.Right,
                    ShelfEdge.Top => box.Top == work.Top, _ => box.Bottom == work.Bottom }, "未贴指定边");
                var manual = new Rect(work.Left + (work.Width - 240) / 2, work.Top + (work.Height - 300) / 2, 240, 300);
                if (edge == ShelfEdge.Left) manual.X = work.Left + 4;
                if (edge == ShelfEdge.Right) manual.X = work.Right - manual.Width - 4;
                if (edge == ShelfEdge.Top) manual.Y = work.Top + 4;
                if (edge == ShelfEdge.Bottom) manual.Y = work.Bottom - manual.Height - 4;
                var detected = DesktopShelfService.Place(manual, work, false, ShelfEdge.None);
                Check(detected.Edge == edge && detected.Position == manual.Location, "关闭吸附后未识别手动靠边，或擅自移动了窗口");
            }
            var recovered = DesktopShelfService.Place(new Rect(9000, -3000, 240, 300), work, false);
            Check(recovered.Edge != ShelfEdge.None && work.Contains(new Rect(recovered.Position, new Size(240, 300))), "离线显示器位置未恢复或未识别靠边");
            var nearest = DesktopShelfService.Place(new Rect(work.Left + 8, work.Top + 80, 240, 300), work, true);
            Check(nearest.Edge == ShelfEdge.Left && nearest.Position.X == work.Left, "没有吸附最近边");
            var middle = new Rect(work.Left + (work.Width - 240) / 2, work.Top + (work.Height - 300) / 2, 240, 300);
            var free = DesktopShelfService.Place(middle, work, false, ShelfEdge.Left);
            Check(free.Edge == ShelfEdge.None && free.Position == middle.Location, "自由位置仍沿用旧停靠边或被吸附");
            var outsideTolerance = DesktopShelfService.Place(new Rect(work.Left + 9, work.Top + 80, 240, 300), work, false);
            Check(outsideTolerance.Edge == ShelfEdge.None, "离开边缘后仍判断为停靠");
        }
    }

    static void Adding() => InTemp(dir =>
    {
        var file = Path.Combine(dir, "中文 空格 & 文件.txt");
        File.WriteAllText(file, "original");
        var script = Path.Combine(dir, "unsafe.cmd");
        File.WriteAllText(script, "not executed");
        var folder = Directory.CreateDirectory(Path.Combine(dir, "资料夹")).FullName;
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "folder content");
        S.Items.Clear();
        var window = new DesktopShelfWindow(() => { });
        try
        {
            window.Show(); Await(window.InventoryReady);
            Await(window.AddPathsAsync([file, folder, file, script, Path.Combine(dir, "missing.txt")]));
            Check(S.Items.Count == 2 && S.Items.All(i => i.IsStored && i.AddedAt is not null), "批量移入或记录错误");
            Check(!File.Exists(file) && !Directory.Exists(folder), "拖入仍然只是入口，原文件没有移走");
            var storedFile = S.Items.Single(i => !i.IsFolder);
            var storedFolder = S.Items.Single(i => i.IsFolder);
            Check(File.ReadAllText(storedFile.Path) == "original" && File.ReadAllText(Path.Combine(storedFolder.Path, "inside.txt")) == "folder content",
                "收纳文件内容错误");
            Await(window.AddPathsAsync([file]));
            Check(S.Items.Count == 2 && File.Exists(script), "重复添加或不支持类型破坏原文件");
            Await(window.RemoveEntryAsync(storedFile)); Await(window.RemoveEntryAsync(storedFolder));
            Check(S.Items.Count == 0 && File.ReadAllText(file) == "original" && File.ReadAllText(Path.Combine(folder, "inside.txt")) == "folder content",
                "解除后未还原文件/目录");
        }
        finally { window.Close(); RestoreTestFiles(dir); }
        Check(SettingsService.Instance.Current.QuickLaunchItems.Count == 0, "添加收纳污染了快捷启动");
    });

    static void Layout(string? preview) => InTemp(dir =>
    {
        var file = Path.Combine(dir, "项目进度与中英文资料 Project notes.txt");
        File.WriteAllText(file, "sample");
        S.Items = [new() { Name = "项目进度与中英文资料 Project notes 长文件名", Path = file },
            new() { Name = "Project files", Path = dir, IsFolder = true },
            new() { Name = "启动程序", Path = Environment.ProcessPath! },
            new() { Name = "离线文档", Path = Path.Combine(dir, "gone.pdf") }];
        foreach (var theme in Enum.GetValues<ShelfTheme>())
        foreach (var size in new[] { (200d, 160d, 48), (280d, 340d, 32), (800d, 700d, 24) })
        {
            S.Theme = theme; S.Width = size.Item1; S.Height = size.Item2; S.IconSize = size.Item3;
            S.CornerRadius = theme == ShelfTheme.Light ? 0 : 12;
            S.BackgroundOpacity = theme == ShelfTheme.Dark ? 0.7 : 0.92;
            var window = new DesktopShelfWindow(() => { });
            try
            {
                window.Show(); Pump(); window.UpdateLayout();
                Wait(() => Descendants<Image>(window).Count() >= 3, "系统图标未加载");
                var buttons = Descendants<Button>(window).Where(b => b.Name is "ShelfSort" or "ShelfSettings").ToArray();
                Check(buttons.Length == 2 && buttons.All(b => b.ActualWidth == Math.Max(32, S.IconSize) && b.ActualHeight == b.ActualWidth), "排序/设置按钮未适配图标");
                foreach (var button in buttons)
                {
                    var at = button.TranslatePoint(new Point(), window);
                    Check(at.X >= 0 && at.X + button.ActualWidth <= window.ActualWidth && at.Y >= 0, "工具按钮溢出");
                }
                Check(Descendants<ScrollViewer>(window).All(s => s.ScrollableWidth < 1), "出现横向滚动");
                foreach (var tile in Descendants<Grid>(window).Where(g => g.Tag is QuickLaunchEntry))
                {
                    var remove = tile.Children.OfType<Button>().Single(b => b.Name == "ShelfRemove");
                    var entry = tile.Children.OfType<Button>().Single(b => b.Name == "ShelfEntry");
                    var icon = Descendants<Grid>(entry).Single(g => g.Width == S.IconSize && g.Height == S.IconSize);
                    remove.Visibility = Visibility.Visible; window.UpdateLayout();
                    var iconBox = new Rect(icon.TranslatePoint(new Point(), tile), icon.RenderSize);
                    var removeBox = new Rect(remove.TranslatePoint(new Point(), tile), remove.RenderSize);
                    var overlap = Rect.Intersect(iconBox, removeBox);
                    Check(overlap.IsEmpty || overlap.Width < 0.1 || overlap.Height < 0.1, "解除按钮遮住图标，点击打开会误移除");
                    remove.Visibility = Visibility.Hidden;
                }
                Wait(() => Descendants<TextBlock>(window).Any(t => t.Text == "! 离线文档"), "失效项未标记");
                if (preview is not null) Snapshot(window, Path.Combine(preview, $"shelf-{theme}-{size.Item1}.png"));
                if (size.Item1 == 800)
                {
                    window.WindowState = WindowState.Maximized; Pump(); window.UpdateLayout();
                    Check(buttons.All(b => b.ActualWidth >= 32), "最大化后控件不可点击");
                    window.WindowState = WindowState.Normal;
                }
                S.Theme = ShelfTheme.Sky; window.ApplySettings();
                S.Theme = ShelfTheme.Dark; window.ApplySettings();
                Check(((SolidColorBrush)window.FindResource("Text")).Color.R > 180, "天蓝切到深色后残留深色文字");
                S.Theme = ShelfTheme.Light; window.ApplySettings();
                Check(((SolidColorBrush)window.FindResource("Text")).Color.R < 80, "深色切到浅色后残留浅色文字");
            }
            finally { window.Close(); }
        }
        S.Items.Clear(); S.Width = 280; S.Height = 240; S.IconSize = 32;
        var empty = new DesktopShelfWindow(() => { });
        try
        {
            empty.Show(); Pump();
            Check(Descendants<TextBlock>(empty).Any(t => t.IsVisible && t.Text.StartsWith("把文件")), "空态指引缺失");
            if (preview is not null) Snapshot(empty, Path.Combine(preview, "shelf-empty.png"));
        }
        finally { empty.Close(); }
    });

    static DragEventArgs DropArgs(IDataObject data, object target)
    {
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 5);
        return (DragEventArgs)ctor.Invoke([data, DragDropKeyStates.None, DragDropEffects.All, target, new Point(30, 80)]);
    }

    static void DropAndRemove() => InTemp(dir =>
    {
        var a = Path.Combine(dir, "First.txt"); var b = Path.Combine(dir, "Second.txt");
        File.WriteAllText(a, "a"); File.WriteAllText(b, "b");
        S.Items = []; S.Width = 280; S.Height = 260; S.IconSize = 32; S.Sort = ShelfSort.NameDescending;
        var window = new DesktopShelfWindow(() => { });
        try
        {
            window.Show(); Pump();
            var drop = DropArgs(new DataObject(DataFormats.FileDrop, new[] { a, b }), window);
            drop.RoutedEvent = DragDrop.PreviewDropEvent;
            window.RaiseEvent(drop);
            Wait(() => S.Items.Count == 2, "窗口拖入未添加"); Pump();
            var entries = Descendants<Button>(window).Where(button => button.Name == "ShelfEntry").ToArray();
            Check(!File.Exists(a) && !File.Exists(b), "Drop 事件没有移动原文件");
            var source = (DesktopShelfEntry)entries[0].Tag;
            var data = new DataObject("FlashTrans.DesktopShelf.Entry", source);
            // 外来同名格式不能改变列表；只有窗口内发起的拖动才允许交换。
            drop = DropArgs(data, entries[1]); drop.RoutedEvent = DragDrop.PreviewDropEvent;
            entries[1].RaiseEvent(drop);
            Check(S.Sort == ShelfSort.NameDescending, "接受了外来换位");
            typeof(DesktopShelfWindow).GetField("_dragItem", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, source);
            drop = DropArgs(data, entries[1]); drop.RoutedEvent = DragDrop.PreviewDropEvent;
            entries[1].RaiseEvent(drop); Pump();
            Check(S.Sort == ShelfSort.Manual && S.Items[0].OriginalPath == a && S.Items[1].OriginalPath == b, "拖放没有交换显示位置");
            Check(S.Items.All(i => i.LaunchCount == 0), "拖放触发了文件打开");
            var sort = Descendants<Button>(window).Single(button => button.Name == "ShelfSort");
            sort.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var option = sort.ContextMenu!.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "文件类型"));
            option.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); sort.ContextMenu.IsOpen = false;
            Check(S.Sort == ShelfSort.Type, "排序按钮未生效");
            var remove = Descendants<Button>(window).First(button => button.Name == "ShelfRemove");
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Wait(() => S.Items.Count == 1, "一键解除没有完成");
            Check(File.ReadAllText(a) == "a" && !File.Exists(b) && File.ReadAllText(S.Items.Single().Path) == "b", "一键解除没有正确还原文件");
        }
        finally { window.Close(); RestoreTestFiles(dir); }
    });

    static string CreateLaunchLink(string dir, string marker)
    {
        var link = Path.Combine(dir, "收纳 启动.lnk");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = shell.CreateShortcut(link);
        try
        {
            shortcut.TargetPath = Environment.ProcessPath!;
            shortcut.Arguments = $"\"{Assembly.GetExecutingAssembly().Location}\" --quick-launch-child \"{marker}\"";
            shortcut.WindowStyle = 7; shortcut.Save();
        }
        finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        return link;
    }

    static void Launch() => InTemp(dir =>
    {
        var marker = Path.Combine(dir, "launched");
        var link = CreateLaunchLink(dir, marker);
        S.Items.Clear();
        var window = new DesktopShelfWindow(() => { });
        try
        {
            window.Show(); Pump(); Await(window.AddPathsAsync([link]));
            var start = Stopwatch.StartNew();
            var task = window.LaunchEntryAsync(S.Items.Single());
            var returned = start.Elapsed.TotalMilliseconds;
            var responsive = false;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => responsive = true);
            Await(task); Wait(() => File.Exists(marker), "快捷方式未实际启动");
            Check(responsive && S.Items[0].LaunchCount == 1 && S.Items[0].LastLaunched is not null && window.IsVisible,
                "启动阻塞 UI、未记录使用次数或关闭了收纳窗");
            Console.WriteLine($"       发起打开返回 {returned:F1}ms，隔离进程完成 {start.Elapsed.TotalMilliseconds:F1}ms");
            Check(!File.Exists(link) && File.Exists(S.Items.Single().Path), "快捷方式仍留在原位置");
            Await(window.LaunchEntryAsync(new DesktopShelfEntry { Name = "missing", Path = Path.Combine(dir, "missing.exe") }));
            Check(Descendants<TextBlock>(window).Any(t => t.Text.StartsWith("打开失败：")), "失败没有可见提示");
        }
        finally { window.Close(); RestoreTestFiles(dir); }
    });

    static void SettingsUi(string? preview)
    {
        S.Items.Clear();
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            ThemeService.ApplyTheme(theme);
            var window = new SettingsWindow(new AppHost()) { Width = 620, Height = 440, ShowActivated = false };
            try
            {
                window.SelectTab("desktopshelf"); window.Show(); Pump(); Await(window.ShelfInventoryReady); window.UpdateLayout();
                var enable = Descendants<CheckBox>(window).Single(b => b.Name == "ShelfEnabled");
                enable.IsChecked = true;
                var radius = Descendants<Slider>(window).Single(s => s.Name == "ShelfRadius");
                radius.Value = 0; radius.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                var opacity = Descendants<Slider>(window).Single(s => s.Name == "ShelfOpacity");
                opacity.Value = 0.55; opacity.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                Descendants<ComboBox>(window).Single(c => c.Name == "ShelfTheme").SelectedIndex = 2;
                foreach (var slider in Descendants<Slider>(window))
                    Check(slider.ActualWidth > 40 && slider.TranslatePoint(new Point(slider.ActualWidth, 0), window).X < window.ActualWidth,
                        "最小设置窗口滑块溢出");
                SettingsService.Instance.Load();
                Check(S.Enabled && S.CornerRadius == 0 && S.BackgroundOpacity == 0.55 && S.Theme == ShelfTheme.Dark, "设置未落盘");
                window.SelectTab("general"); window.SelectTab("desktopshelf"); Pump();
                Check(Descendants<Slider>(window).Single(s => s.Name == "ShelfRadius").Value == 0, "切页丢失设置");
                if (preview is not null) Snapshot(window, Path.Combine(preview, $"settings-shelf-{theme}.png"));
            }
            finally { window.Close(); }
        }
    }

    static void AutoHide(string? preview)
    {
        S.Items.Clear(); S.Width = 280; S.Height = 300; S.CornerRadius = 12; S.IconSize = 32;
        S.SnapToEdge = true; S.AutoHide = true; S.Theme = ShelfTheme.Sky;
        var window = new DesktopShelfWindow(() => { });
        try
        {
            window.Show(); Pump();
            foreach (var edge in new[] { ShelfEdge.Left, ShelfEdge.Right, ShelfEdge.Top, ShelfEdge.Bottom })
            {
                window.ExpandFromEdge();
                window.FitToScreen(edge); window.PersistGeometry();
                var full = new Rect(window.Left, window.Top, window.Width, window.Height);
                var collapse = typeof(DesktopShelfWindow).GetMethod("CollapseToEdge", BindingFlags.Instance | BindingFlags.NonPublic)!;
                collapse.Invoke(window, null); Pump();
                Check(window.IsEdgeCollapsed && window.Topmost, "未收起为可呼出的窄边");
                Check(edge is ShelfEdge.Left or ShelfEdge.Right ? window.Width == 12 && window.Height == 80
                    : window.Height == 12 && window.Width == 80, "隐藏窗口仍占大块桌面");
                window.PersistGeometry();
                Check(Win32.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect), "未取得原生窗口尺寸");
                var dpi = VisualTreeHelper.GetDpi(window);
                Check(Math.Abs(rect.Right - rect.Left - window.Width * dpi.DpiScaleX) <= 1
                    && Math.Abs(rect.Bottom - rect.Top - window.Height * dpi.DpiScaleY) <= 1,
                    "WPF 虽已缩小，原生窗口仍挡住桌面");
                Check(S.Width == full.Width && S.Height == full.Height && S.Left == full.Left && S.Top == full.Top,
                    "收起状态覆盖了正常窗口位置或尺寸");
                if (preview is not null) Snapshot(window, Path.Combine(preview, $"shelf-hidden-{edge}.png"));
                var foreground = Win32.GetForegroundWindow();
                window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                Pump();
                Check(!window.IsEdgeCollapsed && new Rect(window.Left, window.Top, window.Width, window.Height) == full, "移入未恢复原位置/尺寸");
                Check(Win32.GetForegroundWindow() == foreground, "悬停展开抢走了前台焦点");

                var sort = Descendants<Button>(window).Single(b => b.Name == "ShelfSort");
                sort.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Check(!window.TryAutoHide() && !window.IsEdgeCollapsed, "排序菜单打开时误收起");
                sort.ContextMenu!.IsOpen = false;
                foreach (var field in new[] { "_moving", "_resizing", "_dragging", "_dialogOpen", "_adding" })
                {
                    var info = typeof(DesktopShelfWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!;
                    info.SetValue(window, true);
                    Check(!window.TryAutoHide(), "交互中误收起：" + field);
                    info.SetValue(window, false);
                }
                collapse.Invoke(window, null);
                var data = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(Environment.CurrentDirectory, "README.md") });
                var over = DropArgs(data, window); over.RoutedEvent = DragDrop.PreviewDragOverEvent;
                window.RaiseEvent(over);
                Check(!window.IsEdgeCollapsed && over.Effects == DragDropEffects.Move, "拖文件到窄边未展开");
                collapse.Invoke(window, null);
                S.AutoHide = false; window.ApplySettings();
                Check(!window.IsEdgeCollapsed && !window.TryAutoHide(), "关闭自动隐藏后没有展开或仍收起");
                S.AutoHide = true;
            }
            window.ExpandFromEdge(); window.FitToScreen(ShelfEdge.Left);
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            Check(!window.IsEdgeCollapsed, "移出后没有延迟，立即闪退");
            Wait(() => window.IsEdgeCollapsed, "移开后定时收起没有生效");
            window.RevealForUser();
            Check(!window.IsEdgeCollapsed, "托盘呼出没有展开");
            Wait(() => window.IsEdgeCollapsed, "托盘呼出后一直无法自动收起");
            S.SnapToEdge = false; window.ApplySettings();
            Check(window.IsEdgeCollapsed, "只关闭自动贴边却取消了自动隐藏");
            S.AutoHide = false; window.ApplySettings();
            Check(!window.IsEdgeCollapsed && !window.TryAutoHide(), "关闭自动隐藏后没有展开");
        }
        finally { window.Close(); S.AutoHide = false; }
        var reopened = new DesktopShelfWindow(() => { });
        try
        {
            reopened.Show(); Pump();
            Check(reopened.Width == S.Width && reopened.Height == S.Height && reopened.Left == S.Left && reopened.Top == S.Top,
                "关闭/重开后丢失收起前的位置和大小");
        }
        finally { reopened.Close(); }
    }

    static void ManualEdgeAutoHide(string? preview)
    {
        SettingsService.Instance.Current.DesktopShelf = new() { SnapToEdge = false, Width = 280, Height = 300 };
        Win32.GetCursorPos(out var cursor);
        var window = new DesktopShelfWindow(() => { });
        try
        {
            window.Show(); Await(window.InventoryReady); window.Activate(); Pump();
            var work = ScreenHelper.WorkAreaOf(window);
            var center = new Point(work.Left + (work.Width - window.Width) / 2, work.Top + (work.Height - window.Height) / 2);
            foreach (var edge in new[] { ShelfEdge.Left, ShelfEdge.Right, ShelfEdge.Top, ShelfEdge.Bottom })
            {
                window.ExpandFromEdge();
                window.Left = center.X; window.Top = center.Y;
                window.FitToScreen(); window.PersistGeometry(); window.Activate(); Pump();
                Check(S.Edge == ShelfEdge.None && !window.TryAutoHide() && !window.Topmost, "屏幕中间误隐藏或无故置顶");
                var destination = center;
                if (edge == ShelfEdge.Left) destination.X = work.Left + 4;
                if (edge == ShelfEdge.Right) destination.X = work.Right - window.Width - 4;
                if (edge == ShelfEdge.Top) destination.Y = work.Top + 4;
                if (edge == ShelfEdge.Bottom) destination.Y = work.Bottom - window.Height - 4;
                DragShelf(window, destination);
                Wait(() => S.Edge == edge, "真实拖动后未识别停靠边：" + edge);
                Check((new Point(window.Left, window.Top) - destination).Length < 1, "关闭吸附后窗口仍被强制移动到边缘");
                var expanded = new Rect(window.Left, window.Top, window.Width, window.Height);
                if (preview is not null && edge == ShelfEdge.Left) Snapshot(window, Path.Combine(preview, "shelf-default-dark-manual.png"));

                var outside = window.PointToScreen(new Point(work.Left + work.Width / 2 - window.Left, work.Top + work.Height / 2 - window.Top));
                Check(Win32.SetCursorPos((int)outside.X, (int)outside.Y), "无法移开鼠标");
                Wait(() => !window.IsMouseOver, "鼠标未离开收纳窗");
                Wait(() => window.IsEdgeCollapsed, "关闭自动贴边后鼠标移出仍不隐藏：" + edge);
                Check(S.Left == expanded.Left && S.Top == expanded.Top && S.Width == expanded.Width && S.Height == expanded.Height,
                    "手动靠边收起后覆盖了原窗口位置");
                Check(edge switch { ShelfEdge.Left => window.Left == work.Left, ShelfEdge.Right => window.Left + window.Width == work.Right,
                    ShelfEdge.Top => window.Top == work.Top, _ => window.Top + window.Height == work.Bottom }, "窄边没有落在屏幕边缘");
                if (preview is not null) Snapshot(window, Path.Combine(preview, $"shelf-manual-hidden-{edge}.png"));
                var tab = window.PointToScreen(new Point(window.Width / 2, window.Height / 2));
                Check(Win32.SetCursorPos((int)tab.X, (int)tab.Y), "无法移入窄边");
                Wait(() => !window.IsEdgeCollapsed, "真实鼠标移入窄边未展开：" + edge);
                Check(new Rect(window.Left, window.Top, window.Width, window.Height) == expanded, "展开后手动位置或大小丢失");
            }
        }
        finally { window.Close(); S.AutoHide = false; Win32.SetCursorPos(cursor.X, cursor.Y); Pump(); }
    }

    static void DragShelf(DesktopShelfWindow window, Point destination)
    {
        var start = window.PointToScreen(new Point(20, 20));
        var finish = window.PointToScreen(new Point(20 + destination.X - window.Left, 20 + destination.Y - window.Top));
        var desktop = ScreenCapture.VirtualScreen();
        void SendMouse(Point at, uint flags)
        {
            var input = new INPUT { type = Win32.INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT
            {
                dx = (int)Math.Floor((at.X - desktop.Left + .5) * 65536 / (desktop.Right - desktop.Left)),
                dy = (int)Math.Floor((at.Y - desktop.Top + .5) * 65536 / (desktop.Bottom - desktop.Top)),
                dwFlags = flags | 0x0001 | 0x8000 | 0x4000,
            } } };
            Check(Win32.SendInput(1, [input], Marshal.SizeOf<INPUT>()) == 1, "收纳拖动测试输入被系统拒绝");
        }
        // DragMove 使用原生消息循环，鼠标移动和释放由后台发送，不能等 UI 调用返回后再松手。
        Await(Task.Run(() =>
        {
            try
            {
                SendMouse(start, 0); Thread.Sleep(80);
                SendMouse(start, 0x0002); Thread.Sleep(100);
                for (var i = 1; i <= 8; i++) { SendMouse(start + (finish - start) * (i / 8d), 0); Thread.Sleep(25); }
            }
            finally { SendMouse(finish, 0x0004); }
        }));
        Pump();
    }

    static void SettingsMoves() => InTemp(dir =>
    {
        var file = Path.Combine(dir, "设置页移动.txt"); File.WriteAllText(file, "settings move");
        S.Items.Clear();
        var window = new SettingsWindow(new AppHost());
        try
        {
            window.SelectTab("desktopshelf"); window.Show(); Pump(); Await(window.ShelfInventoryReady);
            Await(window.AddShelfPathsAsync([file]));
            Check(S.Items.Count == 1 && !File.Exists(file) && File.ReadAllText(S.Items[0].Path) == "settings move", "设置页未移动文件");
            var remove = Descendants<Button>(window).Single(b => Equals(b.ToolTip, "解除收纳并还原到原位置"));
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => S.Items.Count == 0, "设置页解除未完成");
            Check(File.ReadAllText(file) == "settings move", "设置页解除未还原文件");
        }
        finally { window.Close(); RestoreTestFiles(dir); }
    });

    static void HostLifecycle()
    {
        SettingsService.Instance.Apply(AppSettings.CreateDefault());
        var host = new AppHost();
        try
        {
            host.Start(startHidden: true, diagnostic: true);
            Check(!Application.Current.Windows.OfType<DesktopShelfWindow>().Any(), "默认关闭仍创建收纳窗");
            S.Enabled = true;
            SettingsService.Instance.Touch();
            Check(!Application.Current.Windows.OfType<DesktopShelfWindow>().Any(), "设置更新同步创建窗口，挤占调用路径");
            Wait(() => Application.Current.Windows.OfType<DesktopShelfWindow>().Any(w => w.IsVisible), "空闲后未显示收纳窗");
            S.Enabled = false;
            SettingsService.Instance.Touch();
            Check(!Application.Current.Windows.OfType<DesktopShelfWindow>().Any(), "关闭后窗口未释放");
            S.Enabled = true; SettingsService.Instance.Touch();
            S.Enabled = false; SettingsService.Instance.Touch();
            Pump();
            Check(!Application.Current.Windows.OfType<DesktopShelfWindow>().Any(), "快速切换后迟到回调又打开了窗口");
        }
        finally { host.Dispose(); }
    }

    internal static void Interactive() => InTemp(dir =>
    {
        var original = SettingsService.Instance.Current;
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var host = new AppHost();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(6) };
        var frame = new DispatcherFrame();
        try
        {
            var settings = AppSettings.CreateDefault();
            var file = Path.Combine(dir, "项目文档 Project notes.txt"); File.WriteAllText(file, "Isolated shelf preview");
            var link = CreateLaunchLink(dir, Path.Combine(dir, "launched"));
            settings.DesktopShelf.Enabled = true;
            settings.DesktopShelf.Items = [new() { Name = "项目资料", Path = dir, IsFolder = true },
                new() { Name = "安全启动测试", Path = link }, new() { Name = "Project notes 中英文长标题", Path = file }];
            SettingsService.Instance.Apply(settings);
            host.Start(startHidden: true, diagnostic: true);
            host.ShowSettings("desktopshelf");
            Wait(() => Application.Current.Windows.OfType<DesktopShelfWindow>().Any(w => w.IsVisible), "预览未显示");
            Application.Current.Windows.OfType<DesktopShelfWindow>().Single().Closed += (_, _) => frame.Continue = false;
            timer.Tick += (_, _) => frame.Continue = false;
            timer.Start();
            Console.WriteLine("隔离收纳窗已启动，六分钟后自动结束。");
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop(); host.Dispose();
            foreach (var window in Application.Current.Windows.OfType<Window>().ToArray()) window.Close();
            SettingsService.Instance.Apply(original);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    });

    static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    static void RestoreTestFiles(string directory)
    {
        foreach (var entry in S.Items.Where(i => i.IsStored && i.OriginalPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray())
            Await(DesktopShelfStorage.Default.RestoreAsync(S, entry));
    }

    static void StorageRecovery() => InTemp(dir =>
    {
        var store = new DesktopShelfStorage(Path.Combine(dir, "store"));
        var sourceDir = Directory.CreateDirectory(Path.Combine(dir, "sources")).FullName;
        var source = Path.Combine(sourceDir, "sample.txt");
        File.WriteAllText(source, "stored content");
        var settings = new DesktopShelfSettings { Items = [new() { Name = "自定义名称", Path = source }] };
        Await(store.RecoverAsync(settings));
        Check(File.Exists(source) && !settings.Items[0].IsStored, "恢复旧配置时擅自移动了文件");
        var added = Await(store.ImportAsync(settings, [source]));
        Check(added.Added == 1 && settings.Items.Count == 1 && settings.Items[0].Name == "自定义名称", "旧入口未原位转换");
        var stored = settings.Items[0];
        var saved = JsonSerializer.Serialize(new AppSettings { DesktopShelf = settings }, SettingsJson.Default.AppSettings);
        var loaded = JsonSerializer.Deserialize(saved, SettingsJson.Default.AppSettings)!;
        SettingsService.Normalize(loaded);
        Check(loaded.DesktopShelf.Items[0].StorageId == stored.StorageId && loaded.DesktopShelf.Items[0].OriginalPath == source,
            "保存/归一化丢失还原记录");
        // 模拟文件已移动、settings.json 尚未保存便退出；独立记录仍能恢复它。
        var recordPath = Path.Combine(store.Root, stored.StorageId, "entry.json");
        var record = JsonSerializer.Deserialize(File.ReadAllText(recordPath), SettingsJson.Default.DesktopShelfRecord)!;
        record.State = "importing";
        File.WriteAllText(recordPath, JsonSerializer.Serialize(record, SettingsJson.Default.DesktopShelfRecord));
        var recovered = new DesktopShelfSettings();
        Check(Await(store.RecoverAsync(recovered)) is null && recovered.Items.Count == 1 && File.ReadAllText(recovered.Items[0].Path) == "stored content",
            "中断后无法恢复已移入的文件");
        File.WriteAllText(source, "new source file");
        ThrowsIo(() => Await(store.RestoreAsync(recovered, recovered.Items[0])));
        Check(File.ReadAllText(source) == "new source file" && File.ReadAllText(stored.Path) == "stored content" && recovered.Items.Count == 1,
            "还原覆盖了同名文件或丢失收纳项");
        File.Delete(source); Directory.Delete(sourceDir);
        Await(store.RestoreAsync(recovered, recovered.Items[0]));
        Check(File.ReadAllText(source) == "stored content" && !File.Exists(stored.Path), "原目录删除后不能重新创建并还原");
        File.Delete(source);
        Check(Await(store.RecoverAsync(loaded.DesktopShelf)) is null && loaded.DesktopShelf.Items.Count == 0,
            "还原完成但旧配置未保存时，重启又出现失效项");

        var outside = Path.Combine(dir, "outside.txt"); File.WriteAllText(outside, "do not touch");
        var forged = new DesktopShelfEntry { StorageId = "../outside", OriginalPath = outside, Path = outside };
        ThrowsIo(() => Await(store.RestoreAsync(new DesktopShelfSettings { Items = [forged] }, forged)));
        Check(File.ReadAllText(outside) == "do not touch", "伪造记录改动了收纳目录外文件");
        var invalid = Await(store.ImportAsync(new DesktopShelfSettings(), [dir]));
        Check(invalid.Added == 0 && invalid.Error is not null && Directory.Exists(dir), "允许收纳自身上级目录");
        var a = Directory.CreateDirectory(Path.Combine(dir, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(dir, "b")).FullName;
        var pa = Path.Combine(a, "same.txt"); var pb = Path.Combine(b, "same.txt");
        File.WriteAllText(pa, "first"); File.WriteAllText(pb, "second");
        var names = new DesktopShelfSettings();
        Check(Await(store.ImportAsync(names, [pa, pb])).Added == 2 && names.Items.Select(i => i.Path).Distinct().Count() == 2,
            "不同目录的同名文件发生覆盖");
        foreach (var entry in names.Items.ToArray()) Await(store.RestoreAsync(names, entry));
        Check(File.ReadAllText(pa) == "first" && File.ReadAllText(pb) == "second", "同名文件没有各自还原");
        var folder = Directory.CreateDirectory(Path.Combine(dir, "parent")).FullName;
        var child = Path.Combine(folder, "child.txt"); File.WriteAllText(child, "child");
        var nested = new DesktopShelfSettings();
        Check(Await(store.ImportAsync(nested, [child, folder])).Added == 1 && nested.Items[0].IsFolder,
            "同时拖入父目录与子文件被拆成两个收纳项");
        Await(store.RestoreAsync(nested, nested.Items[0]));
        Check(File.ReadAllText(child) == "child", "父子目录批量移动丢失文件");
    });

    static void ThrowsIo(Action action)
    {
        try { action(); } catch (IOException) { return; }
        throw new InvalidOperationException("应拒绝不安全的文件操作");
    }
    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    static void Wait(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); Thread.Sleep(10); }
        Check(condition(), message);
    }
    static void Await(Task task) { Wait(() => task.IsCompleted, "异步任务超时"); task.GetAwaiter().GetResult(); }
    static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    static void InTemp(Action<string> action)
    {
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp", "shelf-tests"));
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { action(dir); }
        finally
        {
            Check(Path.GetFullPath(dir).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "临时目录越界");
            Directory.Delete(dir, true);
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
}
