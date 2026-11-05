using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>独立的轻量桌面入口；图标与文件访问均延后到后台，不占应用启动路径。</summary>
public sealed class DesktopShelfWindow : Window
{
    const string DragFormat = "FlashTrans.DesktopShelf.Entry";
    static readonly Geometry SortIcon = Geometry.Parse("M2,3 H10 M2,7 H8 M2,11 H6 M12,3 V13 M10,11 L12,13 L14,11");
    readonly Action _manage;
    readonly Border _shell = new() { BorderThickness = new Thickness(1) };
    readonly Border _edgeTab = new() { Name = "ShelfEdgeTab", Visibility = Visibility.Collapsed,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        ToolTip = "桌面收纳 · 移入或拖文件到此处展开" };
    readonly Border _edgeMark = new() { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center };
    readonly WrapPanel _items = new() { Name = "ShelfItems", Margin = new Thickness(4, 4, 0, 4) };
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBlock _title = UiKit.Text("桌面收纳", 14, "Text", FontWeights.SemiBold);
    readonly TextBlock _status = UiKit.Text("拖入添加 · 拖动图标换位", 12, "TextDim");
    readonly TextBlock _empty = UiKit.Text("把文件或文件夹拖到这里\n移入收纳，解除时还原到原位置", 12, "TextDim", wrap: true);
    readonly Button _sort;
    readonly Button _settings;
    readonly Button _add;
    readonly HashSet<string> _launching = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _restoring = new(StringComparer.OrdinalIgnoreCase);
    internal Task InventoryReady { get; private set; } = Task.CompletedTask;
    readonly DispatcherTimer _saveTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _statusTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer _hideTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(800) };
    bool _closed, _positioned, _applying, _adding;
    bool _collapsed, _moving, _resizing, _dragging, _dialogOpen, _keyboardNavigation;
    int _openMenus;
    bool _edgeChanged;
    Rect _expandedBounds;
    internal bool IsEdgeCollapsed => _collapsed;
    int _revision;
    DesktopShelfEntry? _dragItem;
    Point _dragStart;
    static DesktopShelfSettings S => SettingsService.Instance.Current.DesktopShelf;
    static bool CanAutoHide => S.AutoHide && S.Edge != ShelfEdge.None;

    public DesktopShelfWindow(Action manage)
    {
        _manage = manage;
        Title = "闪译 · 桌面收纳";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 12;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        MinWidth = DesktopShelfSettings.MinWidth;
        MinHeight = DesktopShelfSettings.MinHeight;
        MaxWidth = DesktopShelfSettings.MaxWidth;
        MaxHeight = DesktopShelfSettings.MaxHeight;
        Width = S.Width;
        Height = S.Height;
        _sort = Tool(SortIcon, "排序", (_, _) => ShowSortMenu());
        _sort.Name = "ShelfSort";
        _settings = Tool(UiKit.IconSettings, "桌面收纳设置", (_, _) => _manage());
        _settings.Name = "ShelfSettings";
        _add = Tool(UiKit.IconPlus, "添加文件或程序", (_, _) => PickFiles());
        Content = BuildContent();
        AllowDrop = true;
        PreviewDragOver += OnDragOver;
        PreviewDrop += OnDrop;
        DragLeave += (_, _) => { ClearDropFeedback(); ScheduleAutoHide(); };
        MouseEnter += (_, _) => { _hideTimer.Stop(); ExpandFromEdge(); };
        MouseLeave += (_, _) => ScheduleAutoHide();
        MouseMove += (_, _) => _keyboardNavigation = false;
        PreviewMouseDown += (_, _) => _keyboardNavigation = false;
        PreviewKeyDown += (_, _) => { _keyboardNavigation = true; _hideTimer.Stop(); };
        Deactivated += (_, _) => { _keyboardNavigation = false; ScheduleAutoHide(); };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) | Win32.WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(hwnd)?.AddHook(WindowMessage);
        };
        Loaded += (_, _) => { RestorePosition(); InventoryReady = RecoverInventoryAsync(); };
        SizeChanged += (_, _) =>
        {
            _shell.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)), S.CornerRadius, S.CornerRadius);
        };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SettingsService.Instance.Save(); };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); Status("拖入添加 · 拖动图标换位", temporary: false); };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); TryAutoHide(); };
        Closed += (_, _) =>
        {
            _closed = true;
            _statusTimer.Stop();
            _hideTimer.Stop();
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SettingsService.Instance.Save(); }
        };
        ApplySettings();
    }

    Button Tool(Geometry icon, string tooltip, RoutedEventHandler action)
    {
        var button = UiKit.IconButton(icon, tooltip, action, 16);
        button.Focusable = true;
        button.Width = button.Height = 32;
        AutomationProperties.SetName(button, tooltip);
        var style = new Style(typeof(Button), (Style)FindResource("IconBtn"));
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("AccentSoft")));
        style.Triggers.Add(focus);
        button.Style = style;
        var focusBorder = new FrameworkElementFactory(typeof(Border));
        focusBorder.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        focusBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        focusBorder.SetValue(MarginProperty, new Thickness(2));
        focusBorder.SetResourceReference(Border.BorderBrushProperty, "Accent");
        var focusStyle = new Style(typeof(Control));
        focusStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Control)) { VisualTree = focusBorder }));
        button.FocusVisualStyle = focusStyle;
        return button;
    }

    UIElement BuildContent()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new Grid { Name = "ShelfHeader", Margin = new Thickness(8, 4, 4, 4), Background = Brushes.Transparent };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.ToolTip = "按住此处拖动收纳盒，松手后自动贴到最近的屏幕边缘";
        head.Children.Add(_title);
        var tools = UiKit.Row(0, _add, _sort, _settings);
        Grid.SetColumn(tools, 1);
        head.Children.Add(tools);
        head.MouseLeftButtonDown += (_, e) =>
        {
            if (Ancestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
            if (e.ClickCount != 1) return;
            e.Handled = true;
            _moving = true;
            _hideTimer.Stop();
            try { DragMove(); }
            catch (InvalidOperationException) { return; }
            finally { _moving = false; ScheduleAutoHide(); }
            FitToScreen();
            PersistGeometry();
            ScheduleAutoHide();
        };
        grid.Children.Add(head);

        var body = new Grid();
        _scroll.Content = _items;
        _scroll.SetResourceReference(StyleProperty, "PlainScrollViewer");
        body.Children.Add(_scroll);
        _empty.TextAlignment = TextAlignment.Center;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.Margin = new Thickness(16);
        _empty.LineHeight = 20;
        body.Children.Add(_empty);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        var footer = new Grid { Margin = new Thickness(8, 0, 4, 4) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Margin = new Thickness(0, 4, 0, 4);
        footer.Children.Add(_status);
        var grip = new Thumb { Width = 24, Height = 24, Cursor = Cursors.SizeNWSE, ToolTip = "拖动调整大小" };
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetValue(TextBlock.TextProperty, "◢");
        factory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        factory.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        grip.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = factory };
        grip.DragStarted += (_, _) => { _resizing = true; _hideTimer.Stop(); };
        grip.DragDelta += (_, e) =>
        {
            var work = ScreenHelper.WorkAreaOf(this);
            Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, Math.Max(MinWidth, Math.Min(MaxWidth, work.Width)));
            Height = Math.Clamp(Height + e.VerticalChange, MinHeight, Math.Max(MinHeight, Math.Min(MaxHeight, work.Height)));
            Left = Math.Min(Left, work.Right - Width);
            Top = Math.Min(Top, work.Bottom - Height);
        };
        grip.DragCompleted += (_, _) => { _resizing = false; FitToScreen(S.Edge); PersistGeometry(); ScheduleAutoHide(); };
        Grid.SetColumn(grip, 1);
        footer.Children.Add(grip);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        _shell.Child = grid;
        _shell.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        _edgeTab.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        _edgeTab.SetResourceReference(Border.BorderBrushProperty, "Accent");
        _edgeMark.SetResourceReference(Border.BackgroundProperty, "Accent");
        _edgeTab.Child = _edgeMark;
        _edgeTab.MouseLeftButtonDown += (_, e) => { e.Handled = true; ExpandFromEdge(); };
        var root = new Grid();
        root.Children.Add(_shell);
        root.Children.Add(_edgeTab);
        return root;
    }

    public void ApplySettings()
    {
        var wasCollapsed = _collapsed;
        ExpandFromEdge();
        _applying = true;
        try
        {
            Resources.Clear();
            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/FlashTrans;component/Themes/{(S.Theme == ShelfTheme.Dark ? "Dark" : "Light")}.xaml"),
            });
            var sky = S.Theme == ShelfTheme.Sky;
            var dark = S.Theme == ShelfTheme.Dark;
            SetBrush("Accent", dark ? "#7BCBFF" : "#00649B");
            SetBrush("AccentSoft", dark ? "#294B60" : "#C6E8FA");
            if (sky)
            {
                SetBrush("Text", "#12364A"); SetBrush("TextDim", "#34596C");
                SetBrush("BgHover", "#CBE8F8"); SetBrush("BgActive", "#B3DEF5");
                SetBrush("BorderStrong", "#90C4E0");
            }
            var color = ThemeService.Parse(sky ? "#E2F3FC" : dark ? "#20262E" : "#F8FAFC", Colors.White);
            color.A = (byte)Math.Round(S.BackgroundOpacity * 255);
            _shell.Background = new SolidColorBrush(color);
            _shell.CornerRadius = new CornerRadius(S.CornerRadius);
            _shell.Clip = new RectangleGeometry(new Rect(0, 0, Width, Height), S.CornerRadius, S.CornerRadius);
            Topmost = S.Topmost || CanAutoHide;
            _title.ToolTip = S.SnapToEdge ? "按住此处拖动收纳盒，松手后自动贴到最近的屏幕边缘"
                : "按住此处自由拖动；靠近屏幕边缘后可自动隐藏";
            var toolSize = Math.Max(32, S.IconSize);
            foreach (var tool in new[] { _add, _sort, _settings }) tool.Width = tool.Height = toolSize;
            // 最小尺寸也要完整容纳一行大图标及文件名，不能只剩图标上半截。
            MinHeight = Math.Max(DesktopShelfSettings.MinHeight, toolSize + S.IconSize + 96);
            Width = S.Width;
            Height = Math.Max(MinHeight, S.Height);
            if (_positioned) FitToScreen(S.Edge);
            RefreshItems();
        }
        finally { _applying = false; }
        if (wasCollapsed && CanAutoHide) CollapseToEdge();
        else ScheduleAutoHide();
    }

    void SetBrush(string name, string hex) => Resources[name] = new SolidColorBrush(ThemeService.Parse(hex, Colors.Black));

    void RefreshItems()
    {
        var revision = ++_revision;
        var offset = _scroll.VerticalOffset;
        _items.Children.Clear();
        foreach (var item in DesktopShelfService.Ordered(S)) _items.Children.Add(BuildTile(item, revision));
        _empty.Visibility = _items.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _sort.ToolTip = "排序：" + DesktopShelfService.SortOptions.First(o => o.Value == S.Sort).Label;
        _scroll.ScrollToVerticalOffset(offset);
    }

    UIElement BuildTile(DesktopShelfEntry item, int revision)
    {
        var tile = new Grid { Width = S.IconSize + 40, Height = S.IconSize + 44, Margin = new Thickness(2), Tag = item };
        var stack = new StackPanel { Width = S.IconSize + 32, Margin = new Thickness(4, 4, 4, 0) };
        // 右侧预留解除按钮的点击区，不让悬停出现的 × 盖住文件图标。
        var iconHost = new Grid { Width = S.IconSize, Height = S.IconSize, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 0, 0) };
        iconHost.Children.Add(UiKit.Icon(item.IsFolder ? UiKit.IconFolder
            : QuickLaunchService.KindOf(item) == QuickLaunchKind.Program ? QuickLaunchWindow.IconApp : UiKit.IconDocument, S.IconSize - 4));
        stack.Children.Add(iconHost);
        var label = UiKit.Text(item.IsStored ? item.Name : "旧 · " + item.Name, 12);
        label.TextAlignment = TextAlignment.Left;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.Margin = new Thickness(4, 4, 0, 0);
        stack.Children.Add(label);
        var button = Tool(UiKit.IconDocument, item.Name, (_, _) => _ = LaunchEntryAsync(item));
        button.Name = "ShelfEntry";
        button.Width = button.Height = double.NaN;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        button.Content = stack;
        button.Tag = item;
        button.ToolTip = item.Name + "\n" + item.Path + (item.IsStored ? "\n原位置：" + item.OriginalPath : "\n旧入口：尚未移入收纳，右键可移入");
        button.ContextMenu = EntryMenu(item);
        button.IsEnabled = !_launching.Contains(item.Path) && !_restoring.Contains(item.Path);
        tile.Children.Add(button);
        var remove = Tool(UiKit.IconClose, item.IsStored ? "解除收纳并还原到原位置" : "移除旧入口（未移动文件）",
            (_, e) => { e.Handled = true; _ = RemoveEntryAsync(item); });
        remove.IsEnabled = !_restoring.Contains(item.Path) && !_launching.Contains(item.Path);
        remove.Name = "ShelfRemove";
        remove.HorizontalAlignment = HorizontalAlignment.Right;
        remove.VerticalAlignment = VerticalAlignment.Top;
        remove.Visibility = Visibility.Hidden;
        remove.SetResourceReference(BackgroundProperty, "BgAlt");
        AutomationProperties.SetName(remove, "解除收纳：" + item.Name);
        tile.Children.Add(remove);
        void ShowRemove() => remove.Visibility = tile.IsMouseOver || tile.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Hidden;
        tile.MouseEnter += (_, _) => ShowRemove();
        tile.MouseLeave += (_, _) => ShowRemove();
        tile.IsKeyboardFocusWithinChanged += (_, _) => ShowRemove();
        button.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Delete) return;
            e.Handled = true;
            _ = RemoveEntryAsync(item);
        };
        button.PreviewMouseLeftButtonDown += (_, e) => { _dragItem = item; _dragStart = e.GetPosition(this); };
        button.PreviewMouseLeftButtonUp += (_, _) => _dragItem = null;
        button.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || !ReferenceEquals(_dragItem, item)) return;
            var position = e.GetPosition(this);
            if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            e.Handled = true;
            // 只提供进程内格式，拖到资源管理器不会误移动原文件。
            var data = new DataObject(DragFormat, item);
            _dragging = true;
            _hideTimer.Stop();
            try { DragDrop.DoDragDrop(button, data, DragDropEffects.Move); }
            finally { _dragging = false; _dragItem = null; Mouse.Capture(null); ClearDropFeedback(); ScheduleAutoHide(); }
        };
        ShellIcons.Request(item.Path, item.IsFolder, (image, exists) =>
        {
            if (_closed || revision != _revision) return;
            if (image is not null)
            {
                iconHost.Children.Clear();
                var icon = new Image { Source = image, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                iconHost.Children.Add(icon);
            }
            if (exists) return;
            iconHost.Opacity = 0.45;
            label.Text = "! " + item.Name;
            button.ToolTip = "找不到目标，请检查文件是否移动或磁盘是否在线\n" + item.Path;
        });
        return tile;
    }

    ContextMenu EntryMenu(DesktopShelfEntry item)
    {
        var menu = new ContextMenu();
        TrackMenu(menu);
        void Add(string text, Action action)
        {
            var entry = new MenuItem { Header = text };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
        Add("打开", () => _ = LaunchEntryAsync(item));
        if (!item.IsStored) Add("移入收纳（移动原文件）", () => _ = AddPathsAsync([item.Path]));
        Add("打开所在位置", async () =>
        {
            try { await Task.Run(() => QuickLaunchService.OpenLocation(item)); }
            catch (Exception ex) { if (!_closed) Status("打开失败：" + ex.Message, error: true); }
        });
        Add(item.IsStored ? "解除收纳并还原到原位置" : "移除旧入口（未移动文件）", () => _ = RemoveEntryAsync(item));
        return menu;
    }

    internal async Task LaunchEntryAsync(DesktopShelfEntry item)
    {
        if (_adding || _restoring.Contains(item.Path)) return;
        if (!_launching.Add(item.Path)) return;
        _hideTimer.Stop();
        SetEntryEnabled(item, false);
        Status("正在打开「" + item.Name + "」…", temporary: false);
        // ShellExecute 及离线盘验证不能卡 UI；统计回到 UI 线程再写，避免并发覆盖配置。
        var target = new QuickLaunchEntry { Name = item.Name, Path = item.Path, IsFolder = item.IsFolder };
        try
        {
            var launched = await Task.Run(() => QuickLaunchService.Launch(target));
            if (_closed) return;
            if (launched && S.Items.Contains(item))
            {
                item.LaunchCount = Math.Min(int.MaxValue - 1, item.LaunchCount) + 1;
                item.LastLaunched = target.LastLaunched;
                ScheduleSave();
            }
            Status(launched ? "已打开「" + item.Name + "」" : "已取消打开");
        }
        catch (Exception ex) { if (!_closed) Status("打开失败：" + ex.Message, error: true); }
        finally
        {
            _launching.Remove(item.Path);
            if (!_closed)
            {
                if (S.Sort is ShelfSort.MostUsed or ShelfSort.RecentlyUsed) RefreshItems();
                else SetEntryEnabled(item, true);
                ScheduleAutoHide();
            }
        }
    }

    void SetEntryEnabled(QuickLaunchEntry item, bool enabled)
    {
        foreach (var tile in _items.Children.OfType<Grid>().Where(g => ReferenceEquals(g.Tag, item)))
            foreach (var button in tile.Children.OfType<Button>().Where(b => b.Name == "ShelfEntry")) button.IsEnabled = enabled;
    }

    internal async Task RemoveEntryAsync(DesktopShelfEntry item)
    {
        if (_launching.Contains(item.Path) || !_restoring.Add(item.Path)) return;
        _hideTimer.Stop();
        RefreshItems();
        Status(item.IsStored ? "正在还原到原位置…" : "正在移除旧入口…", temporary: false);
        try
        {
            await DesktopShelfStorage.Default.RestoreAsync(S, item);
            SettingsService.Instance.Save();
            if (!_closed) Status(item.IsStored ? "已解除收纳，文件已还原到原位置" : "已移除旧入口，文件未改动");
        }
        catch (Exception ex) { if (!_closed) Status("解除失败：" + ex.Message, error: true, temporary: false); }
        finally { _restoring.Remove(item.Path); if (!_closed) { RefreshItems(); ScheduleAutoHide(); } }
    }

    async Task RecoverInventoryAsync()
    {
        var settings = S;
        try
        {
            var error = await DesktopShelfStorage.Default.RecoverAsync(settings);
            if (!ReferenceEquals(settings, S)) return;
            SettingsService.Instance.Save();
            if (!_closed) { RefreshItems(); if (error is not null) Status(error, error: true, temporary: false); }
        }
        catch (Exception ex) { if (!_closed) Status("读取收纳文件失败：" + ex.Message, error: true, temporary: false); }
    }

    internal async Task AddPathsAsync(IEnumerable<string> paths)
    {
        await InventoryReady;
        if (_adding) { Status("正在添加，请稍候"); return; }
        var settings = S;
        _adding = true;
        _hideTimer.Stop();
        _add.IsEnabled = false;
        Status("正在将文件移入收纳…", temporary: false);
        try
        {
            var (added, error) = await DesktopShelfStorage.Default.ImportAsync(settings, paths);
            if (!ReferenceEquals(settings, S)) return;
            SettingsService.Instance.Save();
            if (!_closed)
            {
                RefreshItems();
                Status($"已移入收纳 {added} 项" + (error is null ? "" : "；" + error), error is not null, temporary: error is null);
            }
        }
        catch (Exception ex) { if (!_closed) Status("添加失败：" + ex.Message, error: true); }
        finally { _adding = false; _add.IsEnabled = true; ScheduleAutoHide(); }
    }

    void PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "添加桌面收纳项目", Multiselect = true,
            Filter = "文件或程序 (*.*)|*.*", CheckFileExists = true, DereferenceLinks = false };
        _dialogOpen = true;
        _hideTimer.Stop();
        try { if (dialog.ShowDialog(this) == true) _ = AddPathsAsync(dialog.FileNames); }
        catch (Exception ex) { Status("选择文件失败：" + ex.Message, error: true); }
        finally { _dialogOpen = false; ScheduleAutoHide(); }
    }

    void ShowSortMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _sort, Placement = PlacementMode.Bottom };
        TrackMenu(menu);
        foreach (var (label, value) in DesktopShelfService.SortOptions)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = S.Sort == value };
            item.Click += (_, _) => { S.Sort = value; RefreshItems(); ScheduleSave(); };
            menu.Items.Add(item);
        }
        _sort.ContextMenu = menu;
        menu.IsOpen = true;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) ExpandFromEdge();
        // OLE 拖放期间不一定发普通 MouseEnter，反复 DragOver 会延后收起。
        ScheduleAutoHide();
        ClearDropFeedback();
        var source = e.Data.GetData(DragFormat) as DesktopShelfEntry;
        var target = Ancestor<Grid>(e.OriginalSource as DependencyObject, g => g.Tag is DesktopShelfEntry);
        if (source is not null && ReferenceEquals(source, _dragItem) && target?.Tag is DesktopShelfEntry other && !ReferenceEquals(source, other))
        {
            e.Effects = DragDropEffects.Move;
            target.SetResourceReference(Panel.BackgroundProperty, "AccentSoft");
        }
        else e.Effects = !_adding && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None;
        if (source is not null && ReferenceEquals(source, _dragItem))
        {
            var point = e.GetPosition(_scroll);
            if (point.Y < 24) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - 16);
            else if (point.Y > _scroll.ActualHeight - 24) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + 16);
        }
        _shell.SetResourceReference(Border.BorderBrushProperty, e.Effects == DragDropEffects.None ? "BorderStrong" : "Accent");
    }

    async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ExpandFromEdge();
        ClearDropFeedback();
        if (e.Data.GetData(DragFormat) is DesktopShelfEntry source && ReferenceEquals(source, _dragItem))
        {
            var target = Ancestor<Grid>(e.OriginalSource as DependencyObject, g => g.Tag is DesktopShelfEntry)?.Tag as DesktopShelfEntry;
            if (target is not null && DesktopShelfService.Swap(S, source.Path, target.Path))
            { RefreshItems(); ScheduleSave(); Status("已交换位置 · 手动排列"); }
            return;
        }
        // 异步移动由本程序完成，不让拖放源把返回的 Move 当作已完成而提前清理文件。
        e.Effects = DragDropEffects.None;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await AddPathsAsync(paths);
    }

    void ClearDropFeedback()
    {
        _shell.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        foreach (var tile in _items.Children.OfType<Grid>()) tile.Background = Brushes.Transparent;
    }

    static T? Ancestor<T>(DependencyObject? child, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (var node = child; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is T typed && (match is null || match(typed))) return typed;
        return null;
    }

    void Status(string message, bool error = false, bool temporary = true)
    {
        _status.Text = message;
        _status.ToolTip = message;
        _status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "TextDim");
        _statusTimer.Stop();
        if (temporary) _statusTimer.Start();
    }

    void ScheduleSave() { _saveTimer.Stop(); _saveTimer.Start(); }

    void RestorePosition()
    {
        if (_positioned) return;
        _positioned = true;
        var work = ScreenHelper.WorkAreaOf(this);
        Left = double.IsFinite(S.Left) ? S.Left : work.Right - Width;
        Top = double.IsFinite(S.Top) ? S.Top : work.Top + 64;
        FitToScreen(S.Edge);
        ScheduleAutoHide();
    }

    internal void FitToScreen(ShelfEdge edge = ShelfEdge.None)
    {
        if (_collapsed) return;
        var work = ScreenHelper.WorkAreaOf(this);
        Width = Math.Min(Width, Math.Max(MinWidth, work.Width));
        Height = Math.Min(Height, Math.Max(MinHeight, work.Height));
        var result = DesktopShelfService.Place(new Rect(Left, Top, Width, Height), work, S.SnapToEdge, edge);
        Left = result.Position.X;
        Top = result.Position.Y;
        _edgeChanged |= S.Edge != result.Edge;
        S.Edge = result.Edge;
        Topmost = S.Topmost || CanAutoHide;
    }

    internal void PersistGeometry()
    {
        if (!_positioned || _applying || _collapsed) return;
        if (!_edgeChanged && S.Left == Left && S.Top == Top && S.Width == Width && S.Height == Height) return;
        _edgeChanged = false;
        S.Left = Left; S.Top = Top; S.Width = Width; S.Height = Height;
        ScheduleSave();
    }

    IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 更换显示器、任务栏位置或 DPI 后，重新夹回当前工作区，不轮询桌面。
        if (msg is 0x007E or 0x001A or 0x02E0 && _positioned)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_closed) return;
                var wasCollapsed = _collapsed;
                ExpandFromEdge();
                FitToScreen(S.Edge); PersistGeometry();
                if (wasCollapsed) CollapseToEdge();
            });
        return IntPtr.Zero;
    }

    void TrackMenu(ContextMenu menu)
    {
        menu.Opened += (_, _) => { _openMenus++; _hideTimer.Stop(); };
        menu.Closed += (_, _) => { _openMenus--; ScheduleAutoHide(); };
    }

    void ScheduleAutoHide(TimeSpan? grace = null)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = grace ?? TimeSpan.FromMilliseconds(800);
        if (!_closed && _positioned && !_collapsed && CanAutoHide) _hideTimer.Start();
    }

    internal bool TryAutoHide()
    {
        if (_closed || !_positioned || _collapsed || !CanAutoHide
            || _moving || _resizing || _dragging || _dialogOpen || _openMenus > 0 || _adding || _launching.Count > 0 || _restoring.Count > 0
            || _keyboardNavigation || IsMouseOver) return false;
        if (Mouse.LeftButton == MouseButtonState.Pressed) { ScheduleAutoHide(); return false; }
        CollapseToEdge();
        return true;
    }

    void CollapseToEdge()
    {
        if (_closed || !_positioned || _collapsed || !CanAutoHide) return;
        _hideTimer.Stop();
        FitToScreen(S.Edge);
        if (!CanAutoHide) return;
        PersistGeometry();
        _expandedBounds = new Rect(Left, Top, Width, Height);
        var work = ScreenHelper.WorkAreaOf(this);
        _collapsed = true;
        _shell.Visibility = Visibility.Collapsed;
        _edgeTab.Visibility = Visibility.Visible;
        MinWidth = MinHeight = 12;
        var vertical = S.Edge is ShelfEdge.Left or ShelfEdge.Right;
        Width = vertical ? 12 : 80;
        Height = vertical ? 80 : 12;
        Left = S.Edge switch
        {
            ShelfEdge.Left => work.Left,
            ShelfEdge.Right => work.Right - Width,
            _ => _expandedBounds.Left + (_expandedBounds.Width - Width) / 2,
        };
        Top = S.Edge switch
        {
            ShelfEdge.Top => work.Top,
            ShelfEdge.Bottom => work.Bottom - Height,
            _ => _expandedBounds.Top + (_expandedBounds.Height - Height) / 2,
        };
        _edgeMark.Width = vertical ? 3 : 24;
        _edgeMark.Height = vertical ? 24 : 3;
        // 仅窄边常驻最上层，覆盖面积受限；不把整个透明窗口留在桌面上挡鼠标。
        Topmost = true;
    }

    internal void ExpandFromEdge()
    {
        if (!_collapsed || _closed) return;
        _hideTimer.Stop();
        _collapsed = false;
        MinWidth = DesktopShelfSettings.MinWidth;
        MinHeight = Math.Max(DesktopShelfSettings.MinHeight, Math.Max(32, S.IconSize) + S.IconSize + 96);
        Width = _expandedBounds.Width;
        Height = _expandedBounds.Height;
        Left = _expandedBounds.Left;
        Top = _expandedBounds.Top;
        _edgeTab.Visibility = Visibility.Collapsed;
        _shell.Visibility = Visibility.Visible;
        FitToScreen(S.Edge);
        Topmost = S.Topmost || CanAutoHide;
        ScheduleAutoHide();
    }

    internal void RevealForUser()
    {
        ExpandFromEdge();
        Topmost = true;
        Activate();
        Topmost = S.Topmost || CanAutoHide;
        _keyboardNavigation = false;
        ScheduleAutoHide(TimeSpan.FromSeconds(2));
    }
}
