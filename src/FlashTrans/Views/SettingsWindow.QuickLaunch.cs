using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        intro.Children.Add(Hint("集中收纳程序、快捷方式、常用文件和文件夹。"));
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
            ActionButton("添加程序", UiKit.IconPlus, () => PickQuickLaunchTarget(QuickLaunchKind.Program), "PrimaryBtn"),
            ActionButton("添加文件", UiKit.IconDocument, () => PickQuickLaunchTarget(QuickLaunchKind.File), "OutlineBtn"),
            ActionButton("添加文件夹", UiKit.IconFolder, () => PickQuickLaunchTarget(QuickLaunchKind.Folder), "OutlineBtn"));
        var listHeader = new DockPanel { Margin = new Thickness(0, 18, 0, 10) };
        DockPanel.SetDock(actions, Dock.Right);
        listHeader.Children.Add(actions);
        listHeader.Children.Add(UiKit.Text($"启动列表 · {S.QuickLaunchItems.Count}", 12, "TextDim", FontWeights.SemiBold));
        page.Children.Add(listHeader);
        var items = new StackPanel();

        if (S.QuickLaunchItems.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(0, 26, 0, 26), HorizontalAlignment = HorizontalAlignment.Center };
            var emptyIcon = QuickLaunchWindow.EntryIcon(QuickLaunchKind.Program);
            emptyIcon.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(emptyIcon);
            var title = UiKit.Text("添加你的第一个启动项目", 13, "Text", FontWeights.SemiBold);
            title.Margin = new Thickness(0, 12, 0, 4);
            empty.Children.Add(title);
            empty.Children.Add(Hint("支持多选，也可以把程序、文件或文件夹直接拖到这一页。"));
            items.Children.Add(empty);
        }
        foreach (var item in S.QuickLaunchItems)
        {
            var index = S.QuickLaunchItems.IndexOf(item);
            var row = new Grid { Margin = new Thickness(12, 10, 10, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
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
            var path = UiKit.Text("", 10.5, "TextDim");
            path.Inlines.Add(new Run(QuickLaunchService.KindName(item) + " · "
                + (item.LaunchCount > 0 ? $"已启动 {item.LaunchCount} 次 · " : "") + QuickLaunchWindow.CompactPath(item.Path, 60)));
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            path.ToolTip = item.Path;
            path.Margin = new Thickness(1, 5, 0, 0);
            text.Children.Add(path);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var icon = QuickLaunchWindow.EntryIcon(item, found =>
            {
                if (found) return;
                var missing = new Run("找不到目标 · ");
                missing.SetResourceReference(TextElement.ForegroundProperty, "Warn");
                path.Inlines.InsertBefore(path.Inlines.FirstInline, missing);
            });
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(icon);

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

        // 整页都接收拖放，这块只负责提示和拖入时的高亮反馈。
        var dropText = UiKit.Row(8, UiKit.Icon(UiKit.IconPlus, 12), UiKit.Text("把程序、文件或文件夹拖到这里即可添加", 11.5, "TextDim"));
        dropText.HorizontalAlignment = HorizontalAlignment.Center;
        var dropHint = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = dropText,
            Padding = new Thickness(12, 11, 12, 11), Margin = new Thickness(0, 10, 0, 0),
        };
        dropHint.SetResourceReference(Border.BackgroundProperty, "BgAlt");
        dropHint.SetResourceReference(Border.BorderBrushProperty, "Border");
        page.Children.Add(dropHint);
        page.Children.Add(Hint("点击名称改名；箭头排序。移除仅影响列表，不删除文件。脚本、安装包等直接执行的文件不能添加。"));
        if (S.QuickLaunchItems.Count > 0)
        {
            var cleanup = SmallButton("清理失效项", CleanupQuickLaunch, "GhostBtn");
            cleanup.ToolTip = "找出目标已不存在的项目，确认后从列表移除";
            cleanup.HorizontalAlignment = HorizontalAlignment.Left;
            cleanup.Margin = new Thickness(-4, 6, 0, 0);
            page.Children.Add(cleanup);
        }
        page.Background = Brushes.Transparent;
        page.AllowDrop = true;
        page.PreviewDragOver += (_, e) =>
        {
            var accepted = DroppedPaths(e.Data).Length > 0;
            e.Effects = accepted ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
            dropHint.SetResourceReference(Border.BorderBrushProperty, accepted ? "Accent" : "Border");
        };
        page.DragLeave += (_, _) => dropHint.SetResourceReference(Border.BorderBrushProperty, "Border");
        page.PreviewDrop += (_, e) =>
        {
            e.Handled = true;
            dropHint.SetResourceReference(Border.BorderBrushProperty, "Border");
            AddQuickLaunchPaths(DroppedPaths(e.Data));
        };
        return page;
    }

    static string[] DroppedPaths(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths ? paths : [];

    /// <summary>存在性检查放后台：离线网络盘上一次 Exists 能卡好几秒。移除前必须确认，离线不等于删了。</summary>
    async void CleanupQuickLaunch()
    {
        var items = S.QuickLaunchItems.ToList();
        Toast("正在检查项目…");
        var missing = await Task.Run(() => QuickLaunchService.FindMissing(items));
        if (missing.Count == 0) { Toast("所有项目都能找到"); return; }
        if (!AppDialog.Confirm(this, "清理失效项目", $"有 {missing.Count} 个项目找不到目标，从列表中移除吗？",
                okText: "移除", tone: DialogTone.Warning,
                detail: "只从列表移除，不删除任何文件。移动硬盘或网络位置暂时离线时，也会显示为找不到：\n"
                    + string.Join("\n", missing.Take(5).Select(m => m.Name)) + (missing.Count > 5 ? "\n…" : "")))
            return;
        foreach (var item in missing) S.QuickLaunchItems.Remove(item);
        Save();
        SelectTab("quicklaunch");
        Toast($"已移除 {missing.Count} 项");
    }

    internal void AddQuickLaunchPaths(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        var (added, lastError) = QuickLaunchService.TryAddPaths(S, paths);
        if (added > 0) Save();
        SelectTab("quicklaunch");
        Toast(lastError is null ? $"已添加 {added} 项" : $"已添加 {added} 项；{lastError}");
    }

    void PickQuickLaunchTarget(QuickLaunchKind kind)
    {
        try
        {
            string[] paths;
            if (kind == QuickLaunchKind.Folder)
            {
                var dialog = new OpenFolderDialog { Title = "添加快速启动文件夹", Multiselect = true };
                if (dialog.ShowDialog(this) != true) return;
                paths = dialog.FolderNames;
            }
            else
            {
                var program = kind == QuickLaunchKind.Program;
                var dialog = new OpenFileDialog
                {
                    Title = program ? "添加快速启动程序" : "添加常用文件",
                    Filter = program ? "程序或快捷方式 (*.exe;*.lnk)|*.exe;*.lnk" : "所有文件 (*.*)|*.*",
                    CheckFileExists = true, Multiselect = true, DereferenceLinks = false,
                };
                if (dialog.ShowDialog(this) != true) return;
                paths = dialog.FileNames;
            }
            AddQuickLaunchPaths(paths);
        }
        catch (Exception ex) { Toast("添加失败：" + ex.Message); }
    }
}
