using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlashTrans.Core;
using FlashTrans.Services;
using Microsoft.Win32;

namespace FlashTrans.Views;

public sealed partial class SettingsWindow
{
    bool _shelfAdding;
    bool _shelfInventoryLoaded;
    string? _shelfBoxId;
    internal Task ShelfInventoryReady { get; private set; } = Task.CompletedTask;

    /// <summary>设置页正在编辑的收纳窗；被删掉或配置被替换后回到第一个。</summary>
    DesktopShelfBox SelectedShelfBox
    {
        get
        {
            var boxes = S.DesktopShelf.Boxes;
            if (boxes.Count == 0) DesktopShelfService.Normalize(S.DesktopShelf);
            return boxes.Find(b => b.Id == _shelfBoxId) ?? S.DesktopShelf.Boxes[0];
        }
    }

    UIElement BuildDesktopShelfPage()
    {
        var shelf = S.DesktopShelf;
        if (!_shelfInventoryLoaded)
        {
            _shelfInventoryLoaded = true;
            Dispatcher.BeginInvoke(() => ShelfInventoryReady = RecoverShelfInventoryAsync());
        }
        var box = SelectedShelfBox;
        _shelfBoxId = box.Id;
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
                "与自动贴边独立；手动靠边也能隐藏，鼠标停在窄条上即展开。"),
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
        Section(page, "外观",
            Field("配色", themes),
            SliderField("圆角（0 为矩形）", "ShelfRadius", shelf.CornerRadius, 0, 24, 4,
                value => shelf.CornerRadius = value, value => $"{value:0}"),
            SliderField("背景不透明度", "ShelfOpacity", shelf.BackgroundOpacity, 0.35, 1, 0.05,
                value => shelf.BackgroundOpacity = value, value => $"{value:P0}"),
            SliderField("图标大小", "ShelfIconSize", shelf.IconSize, 24, 48, 8,
                value => shelf.IconSize = (int)value, value => $"{value:0}"),
            Hint("所有收纳窗共用以上外观。背景变透明时，文字与图标仍保持清晰。"));

        var rows = new List<UIElement>();
        if (shelf.Boxes.Count > 1)
        {
            var picker = Combo(shelf.Boxes.Select(b => (b.Name, b.Id)), box.Id, id =>
            {
                _shelfBoxId = id;
                // 下拉框还在自己的选择事件里，换页放到事件结束之后。
                Dispatcher.BeginInvoke(() => { if (_selectedPageKey == "desktopshelf") SelectTab("desktopshelf"); });
            }, width: double.NaN);
            picker.Name = "ShelfPicker";
            rows.Add(Field("正在设置", picker));
        }
        var name = new TextBox { Name = "ShelfName", Text = box.Name, FontSize = 12.5, Height = 28, MaxLength = 64,
            Padding = new Thickness(7, 0, 7, 0), VerticalContentAlignment = VerticalAlignment.Center };
        void Rename()
        {
            var clean = DesktopShelfService.CleanName(name.Text);
            if (clean.Length == 0 || clean == box.Name) { name.Text = box.Name; return; }
            box.Name = clean;
            Save();
        }
        name.LostFocus += (_, _) => Rename();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Rename(); } };
        rows.Add(Field("名称", name));
        var order = Combo(DesktopShelfService.SortOptions, box.Sort, value => box.Sort = value, width: double.NaN);
        order.Name = "ShelfOrder";
        rows.Add(SliderField("窗口宽度", "ShelfWidth", box.Width, DesktopShelfSettings.MinWidth, DesktopShelfSettings.MaxWidth, 20,
            value => box.Width = value, value => $"{value:0}"));
        rows.Add(SliderField("窗口高度", "ShelfHeight", box.Height, DesktopShelfSettings.MinHeight, DesktopShelfSettings.MaxHeight, 20,
            value => box.Height = value, value => $"{value:0}"));
        rows.Add(Field("排序方式", order));
        var manage = new WrapPanel();
        var create = SmallButton("新建收纳窗", CreateShelfBox);
        create.Name = "ShelfCreate";
        create.IsEnabled = shelf.Boxes.Count < DesktopShelfSettings.MaxBoxes;
        var reveal = SmallButton("显示", () => _host.ShowDesktopShelf(SelectedShelfBox));
        reveal.IsEnabled = shelf.Enabled;
        var delete = SmallButton("删除此收纳窗", DeleteShelfBox);
        delete.Name = "ShelfDelete";
        delete.IsEnabled = shelf.Boxes.Count > 1;
        foreach (var button in new[] { create, reveal, delete })
        {
            button.Height = 32;
            button.Margin = new Thickness(0, 0, 8, 4);
            manage.Children.Add(button);
        }
        rows.Add(manage);
        rows.Add(Hint("拖动图标到另一个收纳窗即可移过去；删除收纳窗时，项目并入其他收纳窗，文件不移动。"));
        Section(page, $"收纳窗 · {shelf.Boxes.Count}", rows.ToArray());

        var actions = new WrapPanel();
        foreach (var (text, folder) in new[] { ("添加文件 / 程序", false), ("添加文件夹", true) })
        {
            var button = SmallButton(text, () => PickShelfPaths(folder));
            button.Height = 32;
            button.Margin = new Thickness(0, 0, 8, 4);
            button.IsEnabled = !_shelfAdding;
            actions.Children.Add(button);
        }
        Section(page, $"收纳项目 · {box.Name} · {box.Items.Count}", actions,
            Hint("旧入口不会自动搬动，点「移入」转换。程序建议收纳快捷方式或整个程序文件夹。"),
            SmallButton("打开收纳目录", OpenShelfStorage),
            Hint("文件保存在配置目录的 desktop-shelf 文件夹内，关闭功能也会保留。"));
        foreach (var item in DesktopShelfService.Ordered(box))
        {
            var row = new Grid { Margin = new Thickness(4, 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(QuickLaunchWindow.EntryIcon(item));
            var label = UiKit.Text(item.IsStored ? item.Name : "未移入 · " + item.Name, 12);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.ToolTip = item.Name + "\n" + item.Path;
            label.Margin = new Thickness(4, 0, 8, 0);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
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
        if (box.Items.Count == 0) page.Children.Add(Hint("还没有项目，拖入常用文件开始收纳。"));
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

    void CreateShelfBox()
    {
        try { _shelfBoxId = DesktopShelfService.AddBox(S.DesktopShelf).Id; }
        catch (InvalidOperationException ex) { Toast(ex.Message); return; }
        Save();
        SelectTab("desktopshelf");
        Toast(S.DesktopShelf.Enabled ? "已新建收纳窗，稍后出现在屏幕边缘" : "已新建收纳窗，开启桌面收纳后显示");
    }

    void DeleteShelfBox()
    {
        if (_shelfAdding) { Toast("文件正在移动，请稍候"); return; }
        var box = SelectedShelfBox;
        if (S.DesktopShelf.Boxes.Count <= 1) { Toast("至少保留一个收纳窗"); return; }
        var heir = S.DesktopShelf.Boxes.First(b => !ReferenceEquals(b, box));
        if (box.Items.Count > 0 && !AppDialog.Confirm(this, "删除收纳窗", $"「{box.Name}」里的 {box.Items.Count} 项会并入「{heir.Name}」。",
                "删除", detail: "只删除这个窗口，收纳目录里的文件不会移动或删除。")) return;
        try { DesktopShelfService.RemoveBox(S.DesktopShelf, box); }
        catch (InvalidOperationException ex) { Toast(ex.Message); return; }
        _shelfBoxId = heir.Id;
        Save();
        SelectTab("desktopshelf");
        Toast($"已删除收纳窗，项目并入「{heir.Name}」");
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
            var (added, error) = await DesktopShelfStorage.Default.ImportAsync(shelf, SelectedShelfBox, paths);
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
