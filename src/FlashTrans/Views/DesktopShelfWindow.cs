using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>独立的轻量桌面入口，每个收纳窗一个窗口；图标与文件访问均延后到后台，不占应用启动路径。</summary>
public sealed class DesktopShelfWindow : Window
{
    const string DragFormat = "FlashTrans.DesktopShelf.Entry";
    // 收起后只留一条贴边窄条；同一条边上的多个窄条之间至少隔开 TabGap，不会叠在一起。
    internal const double TabThickness = 12, TabLength = 72;
    const double TabGap = 8;
    static readonly Geometry SortIcon = Geometry.Parse("M2,3 H10 M2,7 H8 M2,11 H6 M12,3 V13 M10,11 L12,13 L14,11");
    // 指向屏幕内侧的箭头，提示收纳窗从这里拉出；其他停靠边按角度旋转。
    static readonly Geometry TabArrow = Geometry.Parse("M3.5,0 L0,3.5 L3.5,7");
    // 跨收纳窗拖动只认本进程正在拖的那一项，外来的同名格式数据一律拒绝。
    static DesktopShelfEntry? s_dragging;
    // 多个收纳窗共用一次盘点，启动时不必把收纳目录读好几遍。
    static (DesktopShelfSettings Owner, Task<string?> Work)? s_recovery;
    readonly string _id;
    readonly DesktopShelfBox _detached;
    readonly Action _manage;
    readonly Border _shell = new() { BorderThickness = new Thickness(1) };
    readonly Border _edgeTab = new() { Name = "ShelfEdgeTab", Visibility = Visibility.Collapsed, SnapsToDevicePixels = true };
    readonly Path _edgeArrow = new() { Data = TabArrow, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly WrapPanel _items = new() { Name = "ShelfItems", Margin = new Thickness(4, 4, 0, 4) };
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBlock _title = UiKit.Text(DesktopShelfService.DefaultName, 14, "Text", FontWeights.SemiBold);
    readonly TextBox _rename = new() { Name = "ShelfRename", Visibility = Visibility.Collapsed, FontSize = 13,
        Height = 28, Padding = new Thickness(4, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center,
        MaxLength = 64, Margin = new Thickness(0, 0, 6, 0) };
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
    // 鼠标在窄条上停一小会儿才展开：多个窄条挨在一条边上时，划过去不会把它们逐个弹出来。
    readonly DispatcherTimer _revealTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(140) };
    bool _closed, _positioned, _applying, _adding;
    bool _collapsed, _moving, _resizing, _dragging, _dialogOpen, _keyboardNavigation, _renaming, _tabHot;
    int _openMenus;
    bool _edgeChanged;
    Rect _expandedBounds;
    Brush _tabFill = Brushes.Transparent, _tabOnAccent = Brushes.White;
    internal bool IsEdgeCollapsed => _collapsed;
    internal string BoxId => _id;
    int _revision;
    DesktopShelfEntry? _dragItem;
    Point _dragStart;
    static DesktopShelfSettings S => SettingsService.Instance.Current.DesktopShelf;
    // 收纳窗删掉后宿主才关窗，中间这一小段仍落在原对象上，不让界面操作碰到空引用。
    DesktopShelfBox Box => S.Boxes.Find(b => b.Id == _id) ?? _detached;
    bool CanAutoHide => S.AutoHide && Box.Edge != ShelfEdge.None;

    public DesktopShelfWindow(DesktopShelfBox box, Action manage)
    {
        _id = box.Id;
        _detached = box;
        _manage = manage;
        Title = "闪译 · " + box.Name;
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
        Width = box.Width;
        Height = box.Height;
        _sort = Tool(SortIcon, "排序", (_, _) => ShowSortMenu());
        _sort.Name = "ShelfSort";
        _settings = Tool(UiKit.IconSettings, "收纳窗菜单：新建、改名、删除与设置", (_, _) => ShowShelfMenu());
        _settings.Name = "ShelfSettings";
        _add = Tool(UiKit.IconPlus, "添加文件或程序", (_, _) => PickFiles());
        Content = BuildContent();
        AllowDrop = true;
        PreviewDragOver += OnDragOver;
        PreviewDrop += OnDrop;
        DragLeave += (_, _) => { ClearDropFeedback(); ScheduleAutoHide(); };
        MouseEnter += (_, _) => { _hideTimer.Stop(); if (_collapsed) HoverTab(true); };
        MouseLeave += (_, _) => { if (_collapsed) HoverTab(false); else ScheduleAutoHide(); };
        MouseMove += (_, _) => _keyboardNavigation = false;
        PreviewMouseDown += (_, _) => _keyboardNavigation = false;
        PreviewKeyDown += (_, _) => { _keyboardNavigation = true; _hideTimer.Stop(); };
        Deactivated += (_, _) => { _keyboardNavigation = false; EndRename(commit: true); ScheduleAutoHide(); };
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
        _revealTimer.Tick += (_, _) => { _revealTimer.Stop(); if (_collapsed && _tabHot) ExpandFromEdge(animate: true); };
        Closed += (_, _) =>
        {
            _closed = true;
            _statusTimer.Stop();
            _hideTimer.Stop();
            _revealTimer.Stop();
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
        head.Children.Add(_title);
        _rename.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            e.Handled = true;
            EndRename(commit: e.Key == Key.Enter);
        };
        _rename.LostKeyboardFocus += (_, _) => EndRename(commit: true);
        AutomationProperties.SetName(_rename, "收纳窗名称");
        head.Children.Add(_rename);
        var tools = UiKit.Row(0, _add, _sort, _settings);
        Grid.SetColumn(tools, 1);
        head.Children.Add(tools);
        head.MouseLeftButtonDown += (_, e) =>
        {
            if (Ancestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
            if (e.ClickCount == 2) { e.Handled = true; BeginRename(); return; }
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
        // 标题栏和空白处右键也能管理收纳窗；图标上的右键仍是各自的项目菜单。
        foreach (var area in new FrameworkElement[] { head, body })
        {
            area.ContextMenu = new ContextMenu();
            area.ContextMenuOpening += (_, _) => area.ContextMenu = ShelfMenu();
        }

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
        grip.DragCompleted += (_, _) => { _resizing = false; FitToScreen(Box.Edge); PersistGeometry(); ScheduleAutoHide(); };
        Grid.SetColumn(grip, 1);
        footer.Children.Add(grip);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        _shell.Child = grid;
        _shell.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        _edgeTab.Child = _edgeArrow;
        _edgeTab.MouseLeftButtonDown += (_, e) => { e.Handled = true; ExpandFromEdge(animate: true); };
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
            var box = Box;
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
            // 窄条与收纳窗同色，但不跟着调到很透明：常驻屏幕边缘，太淡就找不到了。
            color.A = (byte)Math.Round(Math.Max(S.BackgroundOpacity, 0.9) * 255);
            _tabFill = new SolidColorBrush(color);
            _tabOnAccent = new SolidColorBrush(dark ? Color.FromRgb(0x0E, 0x22, 0x30) : Colors.White);
            _shell.CornerRadius = new CornerRadius(S.CornerRadius);
            _shell.Clip = new RectangleGeometry(new Rect(0, 0, Width, Height), S.CornerRadius, S.CornerRadius);
            Topmost = S.Topmost || CanAutoHide;
            Title = "闪译 · " + box.Name;
            if (!_renaming) _title.Text = box.Name;
            _title.ToolTip = (S.SnapToEdge ? "按住此处拖动收纳盒，松手后自动贴到最近的屏幕边缘"
                : "按住此处自由拖动；靠近屏幕边缘后可自动隐藏") + "\n双击改名";
            var toolSize = Math.Max(32, S.IconSize);
            foreach (var tool in new[] { _add, _sort, _settings }) tool.Width = tool.Height = toolSize;
            // 最小尺寸也要完整容纳一行大图标及文件名，不能只剩图标上半截。
            MinHeight = Math.Max(DesktopShelfSettings.MinHeight, toolSize + S.IconSize + 96);
            Width = box.Width;
            Height = Math.Max(MinHeight, box.Height);
            if (_positioned) FitToScreen(box.Edge);
            RefreshItems();
        }
        finally { _applying = false; }
        PaintTab();
        if (wasCollapsed && CanAutoHide) CollapseToEdge(animate: false);
        else ScheduleAutoHide();
    }

    void SetBrush(string name, string hex) => Resources[name] = new SolidColorBrush(ThemeService.Parse(hex, Colors.Black));

    void RefreshItems()
    {
        var revision = ++_revision;
        var offset = _scroll.VerticalOffset;
        var box = Box;
        _items.Children.Clear();
        foreach (var item in DesktopShelfService.Ordered(box)) _items.Children.Add(BuildTile(item, revision));
        _empty.Visibility = _items.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _sort.ToolTip = "排序：" + DesktopShelfService.SortOptions.First(o => o.Value == box.Sort).Label;
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
            // 只提供进程内格式，拖到资源管理器不会误移动原文件；拖到另一个收纳窗只改归属。
            var data = new DataObject(DragFormat, item);
            _dragging = true;
            s_dragging = item;
            _hideTimer.Stop();
            try { DragDrop.DoDragDrop(button, data, DragDropEffects.Move); }
            finally
            {
                _dragging = false; s_dragging = null; _dragItem = null; Mouse.Capture(null); ClearDropFeedback();
                // 已被拖进别的收纳窗，这里不再显示它。
                if (!_closed && !Box.Items.Contains(item)) RefreshItems();
                ScheduleAutoHide();
            }
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

    ContextMenu ShelfMenu()
    {
        var menu = new ContextMenu();
        TrackMenu(menu);
        void Add(string text, Action action, bool enabled = true)
        {
            var entry = new MenuItem { Header = text, IsEnabled = enabled };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }
        Add("新建收纳窗", CreateShelf, S.Boxes.Count < DesktopShelfSettings.MaxBoxes);
        Add("重命名", BeginRename);
        Add("删除此收纳窗…", DeleteShelf, S.Boxes.Count > 1);
        menu.Items.Add(new Separator());
        Add("收纳设置…", _manage);
        return menu;
    }

    void ShowShelfMenu()
    {
        var menu = ShelfMenu();
        menu.PlacementTarget = _settings;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void CreateShelf()
    {
        try { DesktopShelfService.AddBox(S); }
        catch (InvalidOperationException ex) { Status(ex.Message, error: true); return; }
        // 宿主随后在空闲时建窗口；新收纳窗先展开几秒再按设置收起，用户能看到它出现在哪儿。
        SettingsService.Instance.Touch();
        Status("已新建收纳窗");
    }

    internal void BeginRename()
    {
        if (_closed || _renaming) return;
        ExpandFromEdge();
        _renaming = true;
        _hideTimer.Stop();
        _rename.Text = Box.Name;
        _title.Visibility = Visibility.Collapsed;
        _rename.Visibility = Visibility.Visible;
        // 收纳窗平时不抢焦点，改名时才激活；等菜单收起、焦点归还之后再聚焦输入框。
        Activate();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_renaming) return;
            _rename.Focus();
            _rename.SelectAll();
        });
    }

    internal void EndRename(bool commit)
    {
        if (!_renaming) return;
        _renaming = false;
        var box = Box;
        var name = DesktopShelfService.CleanName(_rename.Text);
        if (commit && name.Length > 0 && name != box.Name)
        {
            box.Name = name;
            Title = "闪译 · " + name;
            ScheduleSave();
        }
        _title.Text = box.Name;
        _rename.Visibility = Visibility.Collapsed;
        _title.Visibility = Visibility.Visible;
        ScheduleAutoHide();
    }

    void DeleteShelf()
    {
        if (_adding || _launching.Count > 0 || _restoring.Count > 0) { Status("正在处理文件，请稍后再删除", error: true); return; }
        var box = Box;
        if (S.Boxes.Count <= 1 || !S.Boxes.Contains(box)) { Status("至少保留一个收纳窗", error: true); return; }
        if (box.Items.Count > 0)
        {
            var heir = S.Boxes.First(b => !ReferenceEquals(b, box));
            _dialogOpen = true;
            _hideTimer.Stop();
            bool confirmed;
            try
            {
                confirmed = AppDialog.Confirm(this, "删除收纳窗", $"「{box.Name}」里的 {box.Items.Count} 项会并入「{heir.Name}」。",
                    "删除", detail: "只删除这个窗口，收纳目录里的文件不会移动或删除。");
            }
            finally { _dialogOpen = false; }
            if (!confirmed || _closed) { ScheduleAutoHide(); return; }
        }
        try { DesktopShelfService.RemoveBox(S, box); }
        catch (InvalidOperationException ex) { Status(ex.Message, error: true); return; }
        // 宿主据此关掉本窗口并刷新接收项目的收纳窗；没有宿主时自己关。
        SettingsService.Instance.Touch();
        if (!_closed) Close();
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
            if (launched && DesktopShelfService.BoxOf(S, item) is not null)
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
                if (Box.Sort is ShelfSort.MostUsed or ShelfSort.RecentlyUsed) RefreshItems();
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
            if (s_recovery is not { } shared || !ReferenceEquals(shared.Owner, settings))
                s_recovery = shared = (settings, DesktopShelfStorage.Default.RecoverAsync(settings));
            var error = await shared.Work;
            if (!ReferenceEquals(settings, S)) return;
            SettingsService.Instance.Save();
            if (_closed) return;
            RefreshItems();
            // 同一条盘点错误只在第一个收纳窗里提示一次。
            if (error is not null && ReferenceEquals(Box, settings.Boxes.FirstOrDefault())) Status(error, error: true, temporary: false);
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
            var (added, error) = await DesktopShelfStorage.Default.ImportAsync(settings, Box, paths);
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
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = Box.Sort == value };
            item.Click += (_, _) => { Box.Sort = value; RefreshItems(); ScheduleSave(); };
            menu.Items.Add(item);
        }
        _sort.ContextMenu = menu;
        menu.IsOpen = true;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var source = e.Data.GetData(DragFormat) as DesktopShelfEntry;
        var moving = source is not null && ReferenceEquals(source, s_dragging);
        if (moving || e.Data.GetDataPresent(DataFormats.FileDrop)) ExpandFromEdge(animate: true);
        // OLE 拖放期间不一定发普通 MouseEnter，反复 DragOver 会延后收起。
        ScheduleAutoHide();
        ClearDropFeedback();
        var own = moving && Box.Items.Contains(source!);
        var target = Ancestor<Grid>(e.OriginalSource as DependencyObject, g => g.Tag is DesktopShelfEntry);
        if (own && target?.Tag is DesktopShelfEntry other && !ReferenceEquals(source, other))
        {
            e.Effects = DragDropEffects.Move;
            target.SetResourceReference(Panel.BackgroundProperty, "AccentSoft");
        }
        else if (moving) e.Effects = own ? DragDropEffects.None : DragDropEffects.Move;
        else e.Effects = !_adding && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None;
        if (own)
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
        if (e.Data.GetData(DragFormat) is DesktopShelfEntry source && ReferenceEquals(source, s_dragging))
        {
            if (Box.Items.Contains(source))
            {
                var target = Ancestor<Grid>(e.OriginalSource as DependencyObject, g => g.Tag is DesktopShelfEntry)?.Tag as DesktopShelfEntry;
                if (target is not null && DesktopShelfService.Swap(Box, source.Path, target.Path))
                { RefreshItems(); ScheduleSave(); Status("已交换位置 · 手动排列"); }
            }
            else MoveHere(source);
            return;
        }
        // 异步移动由本程序完成，不让拖放源把返回的 Move 当作已完成而提前清理文件。
        e.Effects = DragDropEffects.None;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await AddPathsAsync(paths);
    }

    void MoveHere(DesktopShelfEntry entry)
    {
        try { if (!DesktopShelfService.MoveEntry(S, entry, Box)) return; }
        catch (InvalidOperationException ex) { Status(ex.Message, error: true); return; }
        SettingsService.Instance.Save();
        RefreshItems();
        Status("已移入「" + Box.Name + "」，文件位置不变");
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
        var box = Box;
        var work = ScreenHelper.WorkAreaOf(this);
        var fresh = !double.IsFinite(box.Left) || !double.IsFinite(box.Top);
        if (fresh)
        {
            // 新收纳窗沿停靠边找空位，不压住已经摆好的收纳窗。
            var taken = S.Boxes.Where(b => !ReferenceEquals(b, box) && double.IsFinite(b.Left) && double.IsFinite(b.Top))
                .Select(b => new Rect(b.Left, b.Top, b.Width, b.Height)).Where(r => r.IntersectsWith(work)).ToList();
            var (position, edge) = DesktopShelfService.FreeSlot(new Size(Width, Height), work, taken, box.Edge);
            Left = position.X;
            Top = position.Y;
            box.Edge = edge;
        }
        else
        {
            Left = box.Left;
            Top = box.Top;
        }
        FitToScreen(box.Edge);
        if (!fresh) { ScheduleAutoHide(); return; }
        // 立即记下位置，紧接着创建的收纳窗才会避开这里；先展开一会儿，让用户看到它出现在哪儿。
        PersistGeometry();
        ScheduleAutoHide(TimeSpan.FromSeconds(2));
    }

    internal void FitToScreen(ShelfEdge edge = ShelfEdge.None)
    {
        if (_collapsed) return;
        var box = Box;
        var work = ScreenHelper.WorkAreaOf(this);
        Width = Math.Min(Width, Math.Max(MinWidth, work.Width));
        Height = Math.Min(Height, Math.Max(MinHeight, work.Height));
        var result = DesktopShelfService.Place(new Rect(Left, Top, Width, Height), work, S.SnapToEdge, edge);
        Left = result.Position.X;
        Top = result.Position.Y;
        _edgeChanged |= box.Edge != result.Edge;
        box.Edge = result.Edge;
        Topmost = S.Topmost || CanAutoHide;
    }

    internal void PersistGeometry()
    {
        var box = Box;
        if (!_positioned || _applying || _collapsed) return;
        if (!_edgeChanged && box.Left == Left && box.Top == Top && box.Width == Width && box.Height == Height) return;
        _edgeChanged = false;
        box.Left = Left; box.Top = Top; box.Width = Width; box.Height = Height;
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
                FitToScreen(Box.Edge); PersistGeometry();
                if (wasCollapsed) CollapseToEdge(animate: false);
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
            || _moving || _resizing || _dragging || _dialogOpen || _renaming || _openMenus > 0 || _adding || _launching.Count > 0 || _restoring.Count > 0
            || _keyboardNavigation || IsMouseOver) return false;
        // 收起计时只由鼠标离开触发；按着键或停在缝里时稍后再看，不能就此一直展开。
        if (Mouse.LeftButton == MouseButtonState.Pressed || CursorInDockGap()) { ScheduleAutoHide(); return false; }
        CollapseToEdge();
        return true;
    }

    /// <summary>
    /// 关闭吸附后收纳窗可以离屏幕边缘几个像素，窄条却贴在最外侧。鼠标停在这道缝里仍算在收纳窗上，
    /// 否则一展开光标就落在窗外，收起后窄条又正好出现在光标下，来回闪个不停。
    /// </summary>
    bool CursorInDockGap()
    {
        var work = ScreenHelper.WorkAreaOf(this);
        var gap = Box.Edge switch
        {
            ShelfEdge.Left => new Rect(work.Left, Top, Math.Max(0, Left - work.Left), Height),
            ShelfEdge.Right => new Rect(Left + Width, Top, Math.Max(0, work.Right - Left - Width), Height),
            ShelfEdge.Top => new Rect(Left, work.Top, Width, Math.Max(0, Top - work.Top)),
            ShelfEdge.Bottom => new Rect(Left, Top + Height, Width, Math.Max(0, work.Bottom - Top - Height)),
            _ => Rect.Empty,
        };
        return !gap.IsEmpty && gap.Width >= 0.5 && gap.Height >= 0.5 && gap.Contains(ScreenHelper.ToDip(ScreenHelper.CursorPos(), this));
    }

    internal void CollapseToEdge(bool animate = true)
    {
        if (_closed || !_positioned || _collapsed || !CanAutoHide) return;
        _hideTimer.Stop();
        FitToScreen(Box.Edge);
        if (!CanAutoHide) return;
        PersistGeometry();
        _expandedBounds = new Rect(Left, Top, Width, Height);
        var work = ScreenHelper.WorkAreaOf(this);
        var edge = Box.Edge;
        _collapsed = true;
        _tabHot = false;
        _revealTimer.Stop();
        _shell.Visibility = Visibility.Collapsed;
        _edgeTab.Visibility = Visibility.Visible;
        ShapeTab(edge);
        PaintTab();
        MinWidth = MinHeight = TabThickness;
        var vertical = edge is ShelfEdge.Left or ShelfEdge.Right;
        Width = vertical ? TabThickness : TabLength;
        Height = vertical ? TabLength : TabThickness;
        var along = vertical ? _expandedBounds.Top + (_expandedBounds.Height - TabLength) / 2
            : _expandedBounds.Left + (_expandedBounds.Width - TabLength) / 2;
        along = DesktopShelfService.TabOffset(along, TabLength, vertical ? work.Top : work.Left, vertical ? work.Bottom : work.Right,
            OtherTabs(edge, work), TabGap);
        Left = edge switch { ShelfEdge.Left => work.Left, ShelfEdge.Right => work.Right - Width, _ => along };
        Top = edge switch { ShelfEdge.Top => work.Top, ShelfEdge.Bottom => work.Bottom - Height, _ => along };
        // 仅窄边常驻最上层，覆盖面积受限；不把整个透明窗口留在桌面上挡鼠标。
        Topmost = true;
        // 窄条只淡入不平移：它只有十几像素宽，一平移光标所在那几列就空了。
        if (animate) Reveal(_edgeTab, edge, 0);
    }

    IEnumerable<(double Start, double End)> OtherTabs(ShelfEdge edge, Rect work) =>
        Application.Current.Windows.OfType<DesktopShelfWindow>()
            .Where(w => !ReferenceEquals(w, this) && w._collapsed && !w._closed && w.Box.Edge == edge && ScreenHelper.WorkAreaOf(w) == work)
            .Select(w => edge is ShelfEdge.Left or ShelfEdge.Right ? (w.Top, w.Top + w.Height) : (w.Left, w.Left + w.Width));

    void ShapeTab(ShelfEdge edge)
    {
        const double radius = 6;
        // 贴屏幕的一侧是直角且不描边，看起来是从边缘伸出来的把手，而不是一块浮着的按钮。
        (_edgeTab.CornerRadius, _edgeTab.BorderThickness, var angle) = edge switch
        {
            ShelfEdge.Left => (new CornerRadius(0, radius, radius, 0), new Thickness(0, 1, 1, 1), 180d),
            ShelfEdge.Top => (new CornerRadius(0, 0, radius, radius), new Thickness(1, 0, 1, 1), 270d),
            ShelfEdge.Bottom => (new CornerRadius(radius, radius, 0, 0), new Thickness(1, 1, 1, 0), 90d),
            _ => (new CornerRadius(radius, 0, 0, radius), new Thickness(1, 1, 0, 1), 0d),
        };
        _edgeArrow.LayoutTransform = new RotateTransform(angle);
        AutomationProperties.SetName(_edgeTab, "展开收纳窗：" + Box.Name);
    }

    void HoverTab(bool hot)
    {
        _tabHot = hot;
        PaintTab();
        _revealTimer.Stop();
        if (hot) _revealTimer.Start();
    }

    void PaintTab()
    {
        if (_tabHot)
        {
            _edgeTab.SetResourceReference(Border.BackgroundProperty, "Accent");
            _edgeTab.SetResourceReference(Border.BorderBrushProperty, "Accent");
            _edgeArrow.Stroke = _tabOnAccent;
            return;
        }
        _edgeTab.Background = _tabFill;
        _edgeTab.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        _edgeArrow.SetResourceReference(Shape.StrokeProperty, "TextDim");
    }

    /// <summary>
    /// 淡入并从停靠边一侧滑入；系统关闭了界面动画时直接显示。透明窗口里 alpha 为 0 的像素不接鼠标，
    /// 所以不从全透明开始、只朝屏幕边缘方向偏移：光标正停在边缘时，第一帧也接得住，不会穿透到下面的窗口。
    /// </summary>
    static void Reveal(UIElement element, ShelfEdge edge, double distance)
    {
        if (!SystemParameters.ClientAreaAnimation || edge == ShelfEdge.None) return;
        var duration = new Duration(TimeSpan.FromMilliseconds(170));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (distance > 0)
        {
            var shift = new TranslateTransform();
            element.RenderTransform = shift;
            shift.BeginAnimation(edge is ShelfEdge.Left or ShelfEdge.Right ? TranslateTransform.XProperty : TranslateTransform.YProperty,
                new DoubleAnimation(edge is ShelfEdge.Left or ShelfEdge.Top ? -distance : distance, 0, duration)
                    { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    internal void ExpandFromEdge(bool animate = false)
    {
        if (!_collapsed || _closed) return;
        _hideTimer.Stop();
        _revealTimer.Stop();
        _collapsed = false;
        _tabHot = false;
        MinWidth = DesktopShelfSettings.MinWidth;
        MinHeight = Math.Max(DesktopShelfSettings.MinHeight, Math.Max(32, S.IconSize) + S.IconSize + 96);
        Width = _expandedBounds.Width;
        Height = _expandedBounds.Height;
        Left = _expandedBounds.Left;
        Top = _expandedBounds.Top;
        _edgeTab.Visibility = Visibility.Collapsed;
        _shell.Visibility = Visibility.Visible;
        FitToScreen(Box.Edge);
        Topmost = S.Topmost || CanAutoHide;
        if (animate) Reveal(_shell, Box.Edge, 16);
        ScheduleAutoHide();
    }

    internal void RevealForUser()
    {
        ExpandFromEdge(animate: true);
        Topmost = true;
        Activate();
        Topmost = S.Topmost || CanAutoHide;
        _keyboardNavigation = false;
        ScheduleAutoHide(TimeSpan.FromSeconds(2));
    }
}
