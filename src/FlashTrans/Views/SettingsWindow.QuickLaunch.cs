using System.Windows;
using System.Windows.Controls;
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
        Section(page, "呼出面板",
            HotkeyField("呼出 / 隐藏快速启动", S.HkQuickLaunch, v => S.HkQuickLaunch = v, HotkeyAction.QuickLaunch),
            Hint("点输入框直接按组合键；冲突时保留原设置，Backspace 清除。也可从托盘菜单打开。"));

        Button ActionButton(string title, Action action)
        {
            var button = new Button { Content = title, Padding = new Thickness(12, 5, 12, 5) };
            button.SetResourceReference(StyleProperty, "GhostBtn");
            button.Click += (_, _) => action();
            return button;
        }

        var actions = UiKit.Row(8,
            ActionButton("添加程序", () => PickQuickLaunchTarget(folder: false)),
            ActionButton("添加文件夹", () => PickQuickLaunchTarget(folder: true)),
            ActionButton("打开面板", _host.ToggleQuickLaunch));
        Section(page, "启动列表", actions, Hint("可修改显示名称、上下排序；移除只影响列表，不删除原程序或文件夹。"));

        if (S.QuickLaunchItems.Count == 0)
        {
            var hint = Hint("还没有启动项目，点击上方按钮选择添加。");
            if (hint is FrameworkElement element) element.Margin = new Thickness(2, 16, 2, 0);
            page.Children.Add(hint);
        }
        foreach (var item in S.QuickLaunchItems)
        {
            var index = S.QuickLaunchItems.IndexOf(item);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
            var path = UiKit.Text((item.IsFolder ? "文件夹 · " : "程序 · ") + item.Path, 11, "TextDim");
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            path.ToolTip = item.Path;
            path.Margin = new Thickness(1, 5, 0, 0);
            text.Children.Add(path);
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
            tools.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(tools, 1);
            row.Children.Add(tools);
            var card = UiKit.Card(row, new Thickness(10));
            card.Margin = new Thickness(0, 8, 0, 0);
            page.Children.Add(card);
        }
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
