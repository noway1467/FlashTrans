using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using Microsoft.Win32;

namespace FlashTrans.Views;

public sealed partial class SettingsWindow
{
    UIElement BuildQuickLaunchPage()
    {
        var page = Page();
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var open = SmallButton("打开面板", _host.ToggleQuickLaunch, "OutlineBtn");
        open.Height = 30;
        DockPanel.SetDock(open, Dock.Right);
        heading.Children.Add(open);
        var intro = new StackPanel();
        intro.Children.Add(UiKit.Text("常用项目，一键直达", 18, "Text", FontWeights.SemiBold));
        intro.Children.Add(Hint("集中收纳程序、快捷方式和文件夹。"));
        heading.Children.Add(intro);
        page.Children.Add(heading);
        Section(page, "快捷键",
            HotkeyField("呼出 / 隐藏快速启动", S.HkQuickLaunch, v => S.HkQuickLaunch = v, HotkeyAction.QuickLaunch),
            Hint("点击后按组合键，Backspace 清除；也可从托盘打开。"));

        Button ActionButton(string title, Geometry icon, Action action, string style)
        {
            var glyph = UiKit.Icon(icon, 12);
            var label = UiKit.Text(title, 11.5);
            var button = new Button { Content = UiKit.Row(6, glyph, label), Height = 30, Padding = new Thickness(10, 0, 10, 0) };
            button.SetResourceReference(StyleProperty, style);
            glyph.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = button });
            label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = button });
            button.Click += (_, _) => action();
            return button;
        }

        var actions = UiKit.Row(8,
            ActionButton("添加程序", UiKit.IconPlus, () => PickQuickLaunchTarget(folder: false), "PrimaryBtn"),
            ActionButton("添加文件夹", UiKit.IconFolder, () => PickQuickLaunchTarget(folder: true), "OutlineBtn"));
        var listHeader = new DockPanel { Margin = new Thickness(0, 18, 0, 10) };
        DockPanel.SetDock(actions, Dock.Right);
        listHeader.Children.Add(actions);
        listHeader.Children.Add(UiKit.Text($"启动列表 · {S.QuickLaunchItems.Count}", 12, "TextDim", FontWeights.SemiBold));
        page.Children.Add(listHeader);
        var items = new StackPanel();

        if (S.QuickLaunchItems.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(0, 26, 0, 26), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(QuickLaunchWindow.EntryIcon(folder: false));
            var title = UiKit.Text("添加你的第一个启动项目", 13, "Text", FontWeights.SemiBold);
            title.Margin = new Thickness(0, 12, 0, 4);
            empty.Children.Add(title);
            empty.Children.Add(Hint("支持多选程序、快捷方式或文件夹。"));
            items.Children.Add(empty);
        }
        foreach (var item in S.QuickLaunchItems)
        {
            var index = S.QuickLaunchItems.IndexOf(item);
            var row = new Grid { Margin = new Thickness(12, 10, 10, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = QuickLaunchWindow.EntryIcon(item.IsFolder);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(icon);
            var text = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            var name = new TextBox { Name = "QuickLaunchName", Text = item.Name, MaxLength = 80,
                Height = 28, Padding = new Thickness(7, 0, 7, 0), VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "显示名称" };
            name.LostFocus += (_, _) =>
            {
                item.Name = string.IsNullOrWhiteSpace(name.Text) ? QuickLaunchService.DefaultName(item.Path, item.IsFolder) : name.Text.Trim();
                name.Text = item.Name;
                Save();
            };
            text.Children.Add(name);
            var path = UiKit.Text(item.Path, 10.5, "TextDim");
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            path.ToolTip = item.Path;
            path.Margin = new Thickness(1, 5, 0, 0);
            text.Children.Add(path);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            void Move(int delta)
            {
                var from = S.QuickLaunchItems.IndexOf(item);
                var to = from + delta;
                if (from < 0 || to < 0 || to >= S.QuickLaunchItems.Count) return;
                S.QuickLaunchItems.RemoveAt(from);
                S.QuickLaunchItems.Insert(to, item);
                Save();
                SelectTab("quicklaunch");
            }
            var up = UiKit.IconButton(UiKit.IconUp, "上移", (_, _) => Move(-1));
            var down = UiKit.IconButton(UiKit.IconDown, "下移", (_, _) => Move(1));
            up.IsEnabled = index > 0;
            down.IsEnabled = index < S.QuickLaunchItems.Count - 1;
            var remove = UiKit.IconButton(UiKit.IconTrash, "移除（不删除文件）", (_, _) =>
            {
                S.QuickLaunchItems.Remove(item);
                Save();
                SelectTab("quicklaunch");
            });
            var tools = UiKit.Row(2, up, down, remove);
            foreach (var button in new[] { up, down, remove })
            {
                button.Focusable = true;
                System.Windows.Automation.AutomationProperties.SetName(button, button.ToolTip.ToString());
            }
            tools.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(tools, 2);
            row.Children.Add(tools);
            if (index > 0)
            {
                var divider = new Border { Height = 1, Margin = new Thickness(58, 0, 12, 0) };
                divider.SetResourceReference(Border.BackgroundProperty, "Border");
                items.Children.Add(divider);
            }
            items.Children.Add(row);
        }
        page.Children.Add(UiKit.Card(items, new Thickness(0)));
        page.Children.Add(Hint("点击名称改名；箭头排序。移除仅影响列表，不删除文件。"));
        return page;
    }

    void PickQuickLaunchTarget(bool folder)
    {
        try
        {
            string[] paths;
            if (folder)
            {
                var dialog = new OpenFolderDialog { Title = "添加快速启动文件夹", Multiselect = true };
                if (dialog.ShowDialog(this) != true) return;
                paths = dialog.FolderNames;
            }
            else
            {
                var dialog = new OpenFileDialog { Title = "添加快速启动程序", Filter = "程序或快捷方式 (*.exe;*.lnk)|*.exe;*.lnk",
                    CheckFileExists = true, Multiselect = true, DereferenceLinks = false };
                if (dialog.ShowDialog(this) != true) return;
                paths = dialog.FileNames;
            }
            var added = 0;
            string? lastError = null;
            foreach (var path in paths)
            {
                if (QuickLaunchService.TryAdd(S, path, folder, out var error)) added++;
                else lastError = error;
            }
            if (added > 0) Save();
            SelectTab("quicklaunch");
            Toast(lastError is null ? $"已添加 {added} 项" : $"已添加 {added} 项；{lastError}");
        }
        catch (Exception ex) { Toast("添加失败：" + ex.Message); }
    }
}
