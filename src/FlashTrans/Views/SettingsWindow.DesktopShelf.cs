using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlashTrans.Core;
using FlashTrans.Services;
using Microsoft.Win32;

namespace FlashTrans.Views;

public sealed partial class SettingsWindow
{
    bool _shelfAdding;
    bool _shelfInventoryLoaded;
    internal Task ShelfInventoryReady { get; private set; } = Task.CompletedTask;

    UIElement BuildDesktopShelfPage()
    {
        var shelf = S.DesktopShelf;
        if (!_shelfInventoryLoaded)
        {
            _shelfInventoryLoaded = true;
            Dispatcher.BeginInvoke(() => ShelfInventoryReady = RecoverShelfInventoryAsync());
        }
        var page = Page();
        page.Children.Add(UiKit.Text("桌面文件，随手收纳", 18, "Text", FontWeights.SemiBold));
        page.Children.Add(Hint("拖入后移动到收纳目录；解除收纳时还原到原位置，不覆盖同名文件。"));
        var show = SmallButton("显示收纳窗", _host.ShowDesktopShelf);
        show.IsEnabled = shelf.Enabled;
        var enabled = Check("开启桌面收纳", shelf.Enabled, value => { shelf.Enabled = value; show.IsEnabled = value; },
            "默认关闭；开启后随闪译显示，关闭后保留收纳列表。");
        enabled.Name = "ShelfEnabled";
        show.HorizontalAlignment = HorizontalAlignment.Left;
        Section(page, "桌面小窗", enabled, show,
            Check("拖动后自动贴边", shelf.SnapToEdge, value => shelf.SnapToEdge = value),
            Check("贴边后自动隐藏", shelf.AutoHide, value => shelf.AutoHide = value,
                "与自动贴边独立；手动靠边也能隐藏，移入窄边即展开。"),
            Check("始终置顶", shelf.Topmost, value => shelf.Topmost = value,
                "自动隐藏时，窄边与临时展开的小窗始终可见。"));

        UIElement SliderField(string label, string name, double value, double min, double max, double tick,
            Action<double> update, Func<double, string> format)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            var number = UiKit.Text(format(value), 12, "TextDim");
            number.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(number, 1);
            row.Children.Add(number);
            var slider = new Slider { Name = name, Minimum = min, Maximum = max, TickFrequency = tick,
                IsSnapToTickEnabled = true, Value = value, VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, e) => { number.Text = format(e.NewValue); update(e.NewValue); _host.PreviewDesktopShelf(); };
            slider.PreviewMouseLeftButtonUp += (_, _) => Save();
            slider.LostFocus += (_, _) => Save();
            row.Children.Add(slider);
            return Field(label, row);
        }

        var themes = Combo(new[] { ("天蓝色", ShelfTheme.Sky), ("浅色", ShelfTheme.Light), ("深色", ShelfTheme.Dark) },
            shelf.Theme, value => shelf.Theme = value, width: double.NaN);
        themes.Name = "ShelfTheme";
        var order = Combo(DesktopShelfService.SortOptions, shelf.Sort, value => shelf.Sort = value, width: double.NaN);
        order.Name = "ShelfOrder";
        Section(page, "外观与排列",
            Field("配色", themes),
            SliderField("圆角（0 为矩形）", "ShelfRadius", shelf.CornerRadius, 0, 24, 4,
                value => shelf.CornerRadius = value, value => $"{value:0}"),
            SliderField("背景不透明度", "ShelfOpacity", shelf.BackgroundOpacity, 0.35, 1, 0.05,
                value => shelf.BackgroundOpacity = value, value => $"{value:P0}"),
            SliderField("图标大小", "ShelfIconSize", shelf.IconSize, 24, 48, 8,
                value => shelf.IconSize = (int)value, value => $"{value:0}"),
            SliderField("窗口宽度", "ShelfWidth", shelf.Width, DesktopShelfSettings.MinWidth, DesktopShelfSettings.MaxWidth, 20,
                value => shelf.Width = value, value => $"{value:0}"),
            SliderField("窗口高度", "ShelfHeight", shelf.Height, DesktopShelfSettings.MinHeight, DesktopShelfSettings.MaxHeight, 20,
                value => shelf.Height = value, value => $"{value:0}"),
            Field("排序方式", order),
            Hint("拖到另一图标上交换位置，并切回手动排列。背景变透明时，文字与图标仍保持清晰。"));

        var actions = new WrapPanel();
        foreach (var (text, folder) in new[] { ("添加文件 / 程序", false), ("添加文件夹", true) })
        {
            var button = SmallButton(text, () => PickShelfPaths(folder));
            button.Height = 32;
            button.Margin = new Thickness(0, 0, 8, 4);
            button.IsEnabled = !_shelfAdding;
            actions.Children.Add(button);
        }
        Section(page, $"收纳项目 · {shelf.Items.Count}", actions,
            Hint("旧入口不会自动搬动，点「移入」转换。程序建议收纳快捷方式或整个程序文件夹。"),
            SmallButton("打开收纳目录", OpenShelfStorage),
            Hint("文件保存在配置目录的 desktop-shelf 文件夹内，关闭功能也会保留。"));
        foreach (var item in DesktopShelfService.Ordered(shelf))
        {
            var row = new Grid { Margin = new Thickness(4, 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(QuickLaunchWindow.EntryIcon(item));
            var name = UiKit.Text(item.IsStored ? item.Name : "未移入 · " + item.Name, 12);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = item.Name + "\n" + item.Path;
            name.Margin = new Thickness(4, 0, 8, 0);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var remove = UiKit.IconButton(UiKit.IconClose, item.IsStored ? "解除收纳并还原到原位置" : "移除旧入口（未移动文件）",
                async (_, _) => await RestoreShelfEntryAsync(item), 16);
            remove.Width = remove.Height = 32;
            remove.Focusable = true;
            System.Windows.Automation.AutomationProperties.SetName(remove, "解除收纳：" + item.Name);
            var tools = UiKit.Row(0);
            if (!item.IsStored)
            {
                var move = SmallButton("移入", () => _ = AddShelfPathsAsync([item.Path]));
                move.ToolTip = "将旧入口指向的原文件实际移入收纳";
                move.Height = 32;
                tools.Children.Add(move);
            }
            tools.Children.Add(remove);
            Grid.SetColumn(tools, 2);
            row.Children.Add(tools);
            page.Children.Add(row);
        }
        if (shelf.Items.Count == 0) page.Children.Add(Hint("还没有项目，拖入常用文件开始收纳。"));
        page.Background = Brushes.Transparent;
        page.AllowDrop = true;
        page.PreviewDragOver += (_, e) =>
        {
            e.Handled = true;
            e.Effects = !_shelfAdding && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None;
        };
        page.PreviewDrop += async (_, e) => { e.Handled = true; e.Effects = DragDropEffects.None; await AddShelfPathsAsync(DroppedPaths(e.Data)); };
        return page;
    }

    internal async Task AddShelfPathsAsync(IEnumerable<string> paths)
    {
        await ShelfInventoryReady;
        if (_shelfAdding) { Toast("正在添加，请稍候"); return; }
        var shelf = S.DesktopShelf;
        _shelfAdding = true;
        Toast("正在将文件移入收纳…");
        try
        {
            var (added, error) = await DesktopShelfStorage.Default.ImportAsync(shelf, paths);
            if (!ReferenceEquals(shelf, S.DesktopShelf)) return;
            if (added > 0) Save();
            _shelfAdding = false;
            if (_selectedPageKey == "desktopshelf" && IsLoaded) SelectTab("desktopshelf");
            Toast($"已移入收纳 {added} 项" + (error is null ? "" : "；" + error));
        }
        catch (Exception ex) { Toast("添加失败：" + ex.Message); }
        finally { _shelfAdding = false; }
    }

    async Task RestoreShelfEntryAsync(DesktopShelfEntry item)
    {
        if (_shelfAdding) { Toast("文件正在移动，请稍候"); return; }
        _shelfAdding = true;
        try
        {
            await DesktopShelfStorage.Default.RestoreAsync(S.DesktopShelf, item);
            Save();
            _shelfAdding = false;
            if (_selectedPageKey == "desktopshelf" && IsLoaded) SelectTab("desktopshelf");
            Toast(item.IsStored ? "文件已还原到原位置" : "已移除旧入口");
        }
        catch (Exception ex) { AppDialog.Info(this, "解除收纳失败", ex.Message); }
        finally { _shelfAdding = false; }
    }

    async Task RecoverShelfInventoryAsync()
    {
        var shelf = S.DesktopShelf;
        try
        {
            var error = await DesktopShelfStorage.Default.RecoverAsync(shelf);
            if (!ReferenceEquals(shelf, S.DesktopShelf)) return;
            Save();
            if (_selectedPageKey == "desktopshelf" && IsLoaded) SelectTab("desktopshelf");
            if (error is not null) Toast(error);
        }
        catch (Exception ex) { Toast("读取收纳目录失败：" + ex.Message); }
    }

    void OpenShelfStorage()
    {
        try
        {
            System.IO.Directory.CreateDirectory(DesktopShelfStorage.Default.Root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DesktopShelfStorage.Default.Root) { UseShellExecute = true });
        }
        catch (Exception ex) { Toast("打开收纳目录失败：" + ex.Message); }
    }

    async void PickShelfPaths(bool folder)
    {
        try
        {
            if (folder)
            {
                var dialog = new OpenFolderDialog { Title = "添加收纳文件夹", Multiselect = true };
                if (dialog.ShowDialog(this) == true) await AddShelfPathsAsync(dialog.FolderNames);
            }
            else
            {
                var dialog = new OpenFileDialog { Title = "添加桌面收纳项目", Multiselect = true,
                    Filter = "文件或程序 (*.*)|*.*", CheckFileExists = true, DereferenceLinks = false };
                if (dialog.ShowDialog(this) == true) await AddShelfPathsAsync(dialog.FileNames);
            }
        }
        catch (Exception ex) { Toast("添加失败：" + ex.Message); }
    }
}
