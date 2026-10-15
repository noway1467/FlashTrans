using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>搜索优先的轻量启动面板。隐藏后保留窗口，下次呼出重新读取配置。</summary>
public sealed class QuickLaunchWindow : Window
{
    readonly TextBox _search = new() { Name = "QuickLaunchSearch", Tag = "搜索名称或路径", Height = 38,
        FontSize = 15, Padding = new Thickness(11, 0, 11, 0), VerticalContentAlignment = VerticalAlignment.Center };
    readonly ListBox _list = new() { Name = "QuickLaunchList", HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock _empty = UiKit.Text("", 13, "TextDim", wrap: true);
    readonly TextBlock _status = UiKit.Text("↑ ↓ 选择 · Enter 启动 · Esc 关闭", 11, "TextFaint");
    readonly Action _manage;
    static AppSettings S => SettingsService.Instance.Current;

    public QuickLaunchWindow(Action manage)
    {
        _manage = manage;
        Title = "闪译 · 快速启动";
        Width = 560;
        Height = 440;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        Content = BuildContent();
        _search.TextChanged += (_, _) => Refresh();
        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject source
                && ItemsControl.ContainerFromElement(_list, source) is ListBoxItem)
                LaunchSelected();
        };
        PreviewKeyDown += OnKeyDown;
        Deactivated += (_, _) => Hide();
    }

    UIElement BuildContent()
    {
        var grid = new Grid { Margin = new Thickness(18, 12, 18, 12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };
        var close = UiKit.IconButton(UiKit.IconClose, "关闭（Esc）", (_, _) => Hide());
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var manage = new Button { Content = "管理列表", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
        manage.SetResourceReference(StyleProperty, "GhostBtn");
        manage.Click += (_, _) => { Hide(); _manage(); };
        DockPanel.SetDock(manage, Dock.Right);
        header.Children.Add(manage);
        header.Children.Add(UiKit.Text("快速启动", 17, "Text", FontWeights.SemiBold));
        grid.Children.Add(header);
        Grid.SetRow(_search, 1);
        grid.Children.Add(_search);

        var body = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        body.Children.Add(_list);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.TextAlignment = TextAlignment.Center;
        body.Children.Add(_empty);
        Grid.SetRow(body, 2);
        grid.Children.Add(body);
        _status.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_status, 3);
        grid.Children.Add(_status);
        var border = new Border { BorderThickness = new Thickness(1), Child = grid };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        return border;
    }

    public void ShowPanel()
    {
        _search.Clear();
        Refresh();
        var area = ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos(), this);
        Width = Math.Min(560, area.Width);
        Height = Math.Min(440, area.Height);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 3;
        Show();
        Topmost = true;
        Activate();
        Topmost = false;
        _search.Focus();
        Keyboard.Focus(_search);
    }

    public void Refresh()
    {
        var selectedPath = (_list.SelectedItem as ListBoxItem)?.Tag as QuickLaunchEntry;
        _list.Items.Clear();
        foreach (var item in QuickLaunchService.Filter(S.QuickLaunchItems, _search.Text))
        {
            var row = new Grid { Margin = new Thickness(3, 4, 3, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(UiKit.Icon(item.IsFolder ? UiKit.IconFolder : UiKit.IconExpand, 18));
            var text = new StackPanel();
            var name = UiKit.Text(item.Name, 13.5, "Text", FontWeights.SemiBold);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(name);
            var path = UiKit.Text(item.Path, 11, "TextDim");
            path.Margin = new Thickness(0, 3, 0, 0);
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(path);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var entry = new ListBoxItem { Content = row, Tag = item, ToolTip = item.Path,
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            System.Windows.Automation.AutomationProperties.SetName(entry, item.Name);
            _list.Items.Add(entry);
            if (item.Path == selectedPath?.Path) _list.SelectedItem = entry;
        }
        if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
        _empty.Text = S.QuickLaunchItems.Count == 0 ? "还没有启动项目\n点击「管理列表」添加程序或文件夹" : "没有匹配项，试试其他名称或路径";
        _empty.Visibility = _list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
        _status.Text = "↑ ↓ 选择 · Enter 启动 · Esc 关闭";
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Hide(); return; }
        if (!_search.IsKeyboardFocusWithin && !_list.IsKeyboardFocusWithin) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key is Key.Down or Key.Up)
        {
            e.Handled = true;
            if (_list.Items.Count == 0) return;
            _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _list.Items.Count - 1);
            _list.ScrollIntoView(_list.SelectedItem);
        }
        else if (e.Key == Key.Enter) { e.Handled = true; LaunchSelected(); }
    }

    void LaunchSelected()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not QuickLaunchEntry item) return;
        try { QuickLaunchService.Launch(item); Hide(); }
        catch (Exception ex)
        {
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            _status.Text = "启动失败：" + ex.Message;
        }
    }
}
