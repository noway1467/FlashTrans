using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>搜索优先的轻量启动面板。隐藏后保留窗口，下次呼出重新读取配置。</summary>
public sealed class QuickLaunchWindow : Window
{
    // 窗口透明，投影画在这圈外边距里；面板本身尺寸见 PanelWidth/PanelHeight。
    const double ShadowMargin = 16, PanelWidth = 620, PanelHeight = 500;

    static readonly Geometry IconBolt = Geometry.Parse("M9.2,1.5 L3.5,9 H7.6 L6.8,14.5 L12.5,7 H8.4 Z");
    internal static readonly Geometry IconApp = Geometry.Parse("M3,2.5 H13 A1,1 0 0 1 14,3.5 V12.5 A1,1 0 0 1 13,13.5 H3 A1,1 0 0 1 2,12.5 V3.5 A1,1 0 0 1 3,2.5 Z M2,5.5 H14 M4,4 V4.01 M5.6,4 V4.01");

    readonly TextBox _search = new() { Name = "QuickLaunchSearch", Tag = "搜索名称或路径", Height = 44,
        FontSize = 14.5, Padding = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(0),
        Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center };
    readonly ListBox _list = new() { Name = "QuickLaunchList", HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly StackPanel _emptyState = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _empty = UiKit.Text("", 13.5, "Text", FontWeights.SemiBold, wrap: true);
    readonly TextBlock _emptyHint = UiKit.Text("", 11.5, "TextDim", wrap: true);
    readonly Button _emptyAction = new() { Content = "添加项目", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 14, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _status = UiKit.Text("", 11, "TextDim");
    readonly TextBlock _count = UiKit.Text("", 11, "TextDim");
    readonly TextBlock _order = UiKit.Text("", 11, "TextFaint");
    readonly TextBlock _subtitle = UiKit.Text("", 11, "TextDim");
    readonly Border _hotkeyCap;
    readonly Action _manage;
    static AppSettings S => SettingsService.Instance.Current;

    public QuickLaunchWindow(Action manage)
    {
        _manage = manage;
        Title = "闪译 · 快速启动";
        Width = PanelWidth + ShadowMargin * 2;
        Height = PanelHeight + ShadowMargin * 2;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SetResourceReference(ForegroundProperty, "Text");
        _hotkeyCap = KeyCap("");
        Content = BuildContent();
        _search.TextChanged += (_, _) => { _status.Visibility = Visibility.Collapsed; Refresh(); };
        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject source
                && ItemsControl.ContainerFromElement(_list, source) is ListBoxItem)
                LaunchSelected();
        };
        PreviewKeyDown += OnKeyDown;
        // 列表获得焦点后直接打字，转回搜索框，而不是触发列表自带的首字母跳转。
        PreviewTextInput += (_, e) =>
        {
            if (_search.IsKeyboardFocusWithin || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
            e.Handled = true;
            _search.Focus();
            _search.Text += e.Text;
            _search.CaretIndex = _search.Text.Length;
        };
        Deactivated += (_, _) => Hide();
        Activated += (_, _) =>
        {
            if (!_focusSearchOnActivate) return;
            _focusSearchOnActivate = false;
            FocusSearch();
        };
    }

    UIElement BuildContent()
    {
        var grid = new Grid { Margin = new Thickness(20, 16, 20, 12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14), LastChildFill = true };
        var close = UiKit.IconButton(UiKit.IconClose, "关闭（Esc）", (_, _) => Hide());
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var manage = new Button { Content = "管理列表", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        manage.SetResourceReference(StyleProperty, "GhostBtn");
        manage.Click += (_, _) => OpenManager();
        DockPanel.SetDock(manage, Dock.Right);
        header.Children.Add(manage);
        var bolt = UiKit.Icon(IconBolt, 15, thickness: 0);
        bolt.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
        var badge = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Child = bolt };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(UiKit.Text("快速启动", 16, "Text", FontWeights.SemiBold));
        _subtitle.Margin = new Thickness(0, 1, 0, 0);
        titles.Children.Add(_subtitle);
        header.Children.Add(UiKit.Row(11, badge, titles));
        grid.Children.Add(header);

        var search = new Grid();
        search.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        search.ColumnDefinitions.Add(new ColumnDefinition());
        search.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_search, 1);
        search.Children.Add(_search);
        var searchIcon = UiKit.Icon(UiKit.IconSearch, 16);
        searchIcon.IsHitTestVisible = false;
        search.Children.Add(searchIcon);
        // 占位文字让出光标位置，否则闪烁的光标会压在第一个字上。
        var placeholder = UiKit.Text("搜索名称、路径或首字母，如 vsc", 14, "TextFaint");
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(3, 0, 0, 0);
        Grid.SetColumn(placeholder, 1);
        search.Children.Add(placeholder);
        _hotkeyCap.Margin = new Thickness(0, 0, 12, 0);
        _hotkeyCap.ToolTip = "呼出 / 隐藏快速启动";
        Grid.SetColumn(_hotkeyCap, 2);
        search.Children.Add(_hotkeyCap);
        _search.TextChanged += (_, _) => placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var searchBorder = new Border { Child = search, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1) };
        searchBorder.SetResourceReference(Border.BackgroundProperty, "BgInput");
        searchBorder.SetResourceReference(Border.BorderBrushProperty, "Border");
        searchBorder.IsKeyboardFocusWithinChanged += (_, _) => searchBorder.SetResourceReference(Border.BorderBrushProperty,
            searchBorder.IsKeyboardFocusWithin ? "Accent" : "Border");
        Grid.SetRow(searchBorder, 1);
        grid.Children.Add(searchBorder);

        var countRow = new DockPanel { Margin = new Thickness(3, 14, 3, 6) };
        DockPanel.SetDock(_order, Dock.Right);
        countRow.Children.Add(_order);
        countRow.Children.Add(_count);
        Grid.SetRow(countRow, 2);
        grid.Children.Add(countRow);

        var body = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        _list.Padding = new Thickness(0);
        body.Children.Add(_list);
        var emptyIcon = EntryIcon(QuickLaunchKind.Program);
        emptyIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyState.Children.Add(emptyIcon);
        _empty.TextAlignment = TextAlignment.Center;
        _empty.Margin = new Thickness(0, 12, 0, 4);
        _emptyState.Children.Add(_empty);
        _emptyHint.TextAlignment = TextAlignment.Center;
        _emptyHint.MaxWidth = 340;
        _emptyState.Children.Add(_emptyHint);
        _emptyAction.SetResourceReference(StyleProperty, "OutlineBtn");
        _emptyAction.Click += (_, _) => OpenManager();
        _emptyState.Children.Add(_emptyAction);
        body.Children.Add(_emptyState);
        Grid.SetRow(body, 3);
        grid.Children.Add(body);

        var footer = new StackPanel();
        var keys = new WrapPanel();
        foreach (var (key, label) in new[] { ("↑ ↓", "选择"), ("Enter", "打开"), ("Ctrl+Enter", "所在位置"), ("Alt+1–9", "直达"), ("右键", "更多操作") })
        {
            var cap = KeyCap(key);
            var text = UiKit.Text(label, 11, "TextDim");
            text.Margin = new Thickness(5, 0, 14, 0);
            // 不用 UiKit.Row：它会把第二个子元素的 Margin 改成统一间距，右侧留白就丢了。
            var pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            pair.Children.Add(cap);
            pair.Children.Add(text);
            keys.Children.Add(pair);
        }
        footer.Children.Add(keys);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Margin = new Thickness(0, 7, 0, 0);
        footer.Children.Add(_status);
        var footerBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 11, 0, 0), Child = footer };
        footerBorder.SetResourceReference(Border.BorderBrushProperty, "Border");
        Grid.SetRow(footerBorder, 4);
        grid.Children.Add(footerBorder);

        var border = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = grid,
            Margin = new Thickness(ShadowMargin),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = 0.38, Color = Colors.Black },
        };
        border.SetResourceReference(Border.BackgroundProperty, "Bg");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        return border;
    }

    static Border KeyCap(string text)
    {
        var key = new Border { Child = UiKit.Text(text, 10.5, "TextDim"), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 2), BorderThickness = new Thickness(1, 1, 1, 2), VerticalAlignment = VerticalAlignment.Center };
        key.SetResourceReference(Border.BackgroundProperty, "BgAlt");
        key.SetResourceReference(Border.BorderBrushProperty, "Border");
        return key;
    }

    /// <summary>矢量占位图标，类型一眼可辨；真实图标取到之前、取不到时都用它。</summary>
    internal static Border EntryIcon(QuickLaunchKind kind)
    {
        var icon = UiKit.Icon(kind switch
        {
            QuickLaunchKind.Folder => UiKit.IconFolder,
            QuickLaunchKind.File => UiKit.IconDocument,
            _ => IconApp,
        }, 18);
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, kind == QuickLaunchKind.Program ? "Accent" : "TextDim");
        var tile = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Child = icon,
            VerticalAlignment = VerticalAlignment.Center };
        tile.SetResourceReference(Border.BackgroundProperty, kind == QuickLaunchKind.Program ? "AccentSoft" : "BgHover");
        return tile;
    }

    /// <summary>先给占位图标，后台取到系统图标后替换；目标不在时调暗并通知调用方标出来。</summary>
    internal static Border EntryIcon(QuickLaunchEntry item, Action<bool>? exists = null)
    {
        var tile = EntryIcon(QuickLaunchService.KindOf(item));
        ShellIcons.Request(item.Path, item.IsFolder, (image, found) =>
        {
            if (image is not null)
            {
                tile.Background = Brushes.Transparent;
                var view = new Image { Source = image, Width = 32, Height = 32, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.HighQuality);
                tile.Child = view;
            }
            if (!found) tile.Opacity = 0.45;
            exists?.Invoke(found);
        });
        return tile;
    }

    /// <summary>路径太长时保留盘符开头和文件名结尾，中间省略，避免关键的文件名被截掉。</summary>
    internal static string CompactPath(string path, int max = 70)
    {
        if (path.Length <= max) return path;
        var head = path.IndexOf('\\', Math.Min(path.Length - 1, 3));
        head = head is > 0 and < 24 ? head + 1 : 20;
        var tail = max - head - 1;
        var cut = path.IndexOf('\\', path.Length - tail);
        return path[..head] + "…" + (cut > 0 ? path[cut..] : path[^tail..]);
    }

    public void ShowPanel()
    {
        _search.Clear();
        _status.Visibility = Visibility.Collapsed;
        Refresh();
        var hotkey = HotkeySpec.Parse(S.HkQuickLaunch).ToString();
        ((TextBlock)_hotkeyCap.Child).Text = hotkey;
        _hotkeyCap.Visibility = string.IsNullOrEmpty(S.HkQuickLaunch) ? Visibility.Collapsed : Visibility.Visible;
        var area = ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos(), this);
        Width = Math.Min(PanelWidth + ShadowMargin * 2, area.Width);
        Height = Math.Min(PanelHeight + ShadowMargin * 2, area.Height);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 3;
        Show();
        Topmost = true;
        Activate();
        Topmost = false;
        FocusSearch();
        // 激活消息可能晚于这里才处理完，WPF 届时会按自己的记录恢复焦点（自测里偶发落到列表项上）。
        // 激活后和输入队列空闲时各再聚焦一次，保证呼出后直接就能打字搜索。
        _focusSearchOnActivate = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusSearch);
    }

    bool _focusSearchOnActivate;

    void FocusSearch()
    {
        if (!IsVisible) return;
        _search.Focus();
        Keyboard.Focus(_search);
    }

    public void Refresh()
    {
        var selectedPath = (_list.SelectedItem as ListBoxItem)?.Tag as QuickLaunchEntry;
        _list.Items.Clear();
        var query = _search.Text;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var item in QuickLaunchService.Search(S.QuickLaunchItems, query))
        {
            var index = _list.Items.Count;
            var row = new Grid { Margin = new Thickness(2, 3, 2, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = Highlight(item.Name, terms);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(name);
            var path = UiKit.Text("", 11, "TextDim");
            path.Inlines.Add(new Run(CompactPath(item.Path)));
            path.Margin = new Thickness(0, 3, 0, 0);
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(path);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var icon = EntryIcon(item, found =>
            {
                if (found) return;
                var missing = new Run("找不到目标 · ");
                missing.SetResourceReference(TextElement.ForegroundProperty, "Warn");
                path.Inlines.InsertBefore(path.Inlines.FirstInline, missing);
            });
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(icon);
            var side = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
            side.Children.Add(UiKit.Text(QuickLaunchService.KindName(item), 10.5, "TextFaint"));
            if (index < 9)
            {
                var cap = KeyCap($"Alt+{index + 1}");
                cap.Margin = new Thickness(10, 0, 0, 0);
                side.Children.Add(cap);
            }
            Grid.SetColumn(side, 2);
            row.Children.Add(side);
            var entry = new ListBoxItem { Content = row, Tag = item, ToolTip = item.Path, Padding = new Thickness(8, 6, 8, 6),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, ContextMenu = BuildMenu(item) };
            System.Windows.Automation.AutomationProperties.SetName(entry, item.Name);
            _list.Items.Add(entry);
            if (item.Path == selectedPath?.Path) _list.SelectedItem = entry;
        }
        if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
        var none = S.QuickLaunchItems.Count == 0;
        _empty.Text = none ? "还没有启动项目" : "没有匹配项";
        _emptyHint.Text = none ? "在设置里添加程序、文件或文件夹，也可以直接拖进设置页" : "试试其他名称、路径或首字母缩写。";
        _emptyAction.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        _emptyState.Visibility = _list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _subtitle.Text = none ? "收纳常用程序、文件和文件夹" : $"{S.QuickLaunchItems.Count} 个项目";
        _count.Text = terms.Length == 0 ? $"全部项目 · {_list.Items.Count}" : $"搜索结果 · {_list.Items.Count}";
        _order.Text = terms.Length == 0 || _list.Items.Count < 2 ? "" : "按匹配度和使用次数排序";
    }

    static TextBlock Highlight(string text, string[] terms)
    {
        var block = UiKit.Text("", 13.5, "Text", FontWeights.SemiBold);
        var marks = new bool[text.Length];
        foreach (var term in terms)
        {
            var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            for (var i = at; at >= 0 && i < at + term.Length; i++) marks[i] = true;
        }
        for (var start = 0; start < text.Length;)
        {
            var end = start;
            while (end < text.Length && marks[end] == marks[start]) end++;
            var run = new Run(text[start..end]);
            if (marks[start]) run.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            block.Inlines.Add(run);
            start = end;
        }
        return block;
    }

    ContextMenu BuildMenu(QuickLaunchEntry item)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string gesture, Action action)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
            return mi;
        }
        Item("打开", "Enter", () => Launch(item));
        if (QuickLaunchService.CanRunAsAdmin(item)) Item("以管理员身份运行", "Ctrl+Shift+Enter", () => Launch(item, asAdmin: true));
        Item("打开所在位置", "Ctrl+Enter", () => OpenLocation(item));
        Item("复制路径", "", () => CopyPath(item));
        menu.Items.Add(new Separator());
        Item("从列表移除", "", () => Remove(item));
        return menu;
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Hide(); return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // 只按 Alt 时 WPF 报 Key.System；Ctrl+Alt（含 AltGr 输入字符）走普通按键，不会误触直达。
        if (e.Key == Key.System && DigitOf(key) is { } digit)
        {
            // Alt+数字直达当前列表的第 N 项，搜索过滤后同样按显示顺序编号。
            e.Handled = true;
            if (digit <= _list.Items.Count) { _list.SelectedIndex = digit - 1; LaunchSelected(); }
            return;
        }
        if (!_search.IsKeyboardFocusWithin && !_list.IsKeyboardFocusWithin) return;
        var modifiers = Keyboard.Modifiers;
        if (key is Key.Down or Key.Up or Key.PageDown or Key.PageUp && modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (_list.Items.Count == 0) return;
            var step = key switch { Key.Down => 1, Key.Up => -1, Key.PageDown => 5, _ => -5 };
            _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + step, 0, _list.Items.Count - 1);
            _list.ScrollIntoView(_list.SelectedItem);
        }
        else if (key == Key.Enter)
        {
            e.Handled = true;
            if ((_list.SelectedItem as ListBoxItem)?.Tag is not QuickLaunchEntry item) return;
            if (modifiers == ModifierKeys.None) Launch(item);
            else if (modifiers == ModifierKeys.Control) OpenLocation(item);
            else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && QuickLaunchService.CanRunAsAdmin(item)) Launch(item, asAdmin: true);
        }
    }

    static int? DigitOf(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null,
    };

    void LaunchSelected()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is QuickLaunchEntry item) Launch(item);
    }

    void Launch(QuickLaunchEntry item, bool asAdmin = false)
    {
        try
        {
            if (!QuickLaunchService.Launch(item, asAdmin)) { ShowStatus("已取消以管理员身份运行", error: false); return; }
            // 只落盘不广播：使用次数只影响搜索排序，没必要让其他窗口跟着重建。
            SettingsService.Instance.Save();
            Hide();
        }
        catch (Exception ex) { ShowStatus("启动失败：" + ex.Message, error: true); }
    }

    void OpenLocation(QuickLaunchEntry item)
    {
        try { QuickLaunchService.OpenLocation(item); Hide(); }
        catch (Exception ex) { ShowStatus("无法打开所在位置：" + ex.Message, error: true); }
    }

    void CopyPath(QuickLaunchEntry item)
    {
        try { Clipboard.SetDataObject(item.Path, true); ShowStatus("已复制路径", error: false); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        { ShowStatus("剪贴板被其他程序占用，请稍后再试", error: true); }
    }

    void Remove(QuickLaunchEntry item)
    {
        S.QuickLaunchItems.Remove(item);
        SettingsService.Instance.Save();
        Refresh();
        ShowStatus($"已从列表移除「{item.Name}」，文件未删除", error: false);
    }

    void OpenManager() { Hide(); _manage(); }

    void ShowStatus(string message, bool error)
    {
        _status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "TextDim");
        _status.Text = message;
        _status.ToolTip = message;
        _status.Visibility = Visibility.Visible;
    }
}
