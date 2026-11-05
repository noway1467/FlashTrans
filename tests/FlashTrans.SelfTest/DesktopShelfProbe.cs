using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    static DesktopShelfBox Box => S.Boxes[0];
    static readonly ShelfEdge[] Edges = [ShelfEdge.Left, ShelfEdge.Right, ShelfEdge.Top, ShelfEdge.Bottom];
    static readonly FieldInfo Dragging = typeof(DesktopShelfWindow).GetField("s_dragging", BindingFlags.Static | BindingFlags.NonPublic)!;

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
            step("桌面收纳：1.9.7 单窗口配置迁移为收纳窗列表，项目与位置不丢", Migration);
            step("桌面收纳：七种排序、显示顺序交换与离线项目保留", Ordering);
            step("桌面收纳：四边吸附、负坐标屏幕及脱离显示器恢复", Placement);
            step("桌面收纳：多收纳窗新建/删除并入/移动归属、新窗空位与窄条错开", MultipleBoxes);
            step("桌面收纳：真实移动文件/文件夹、去重与还原", Adding);
            step("桌面收纳：重启恢复、旧入口转换、同名保护与路径边界", StorageRecovery);
            step("桌面收纳：跨收纳窗去重、盘点补回、旧入口留在原收纳窗", StorageAcrossBoxes);
            step("桌面收纳：三种配色、最小/常用/大尺寸、空态及长名称", () => Layout(preview));
            step("桌面收纳：WPF 拖放添加、换位、拒绝外来换位与解除", DropAndRemove);
            step("桌面收纳：图标拖到另一个收纳窗只改归属，文件不动并落盘", CrossWindowDrag);
            step("桌面收纳：真实启动快捷方式、UI 响应与失败提示", Launch);
            step("桌面收纳：独立设置页、外观保存与重新载入", () => SettingsUi(preview));
            step("桌面收纳：设置页真实移入和解除还原", SettingsMoves);
            step("桌面收纳：设置页新建、切换、改名、单独调尺寸与删除收纳窗", SettingsBoxes);
            step("桌面收纳：收纳窗菜单与双击标题改名", RenameAndMenu);
            step("桌面收纳：四边收起/展开、菜单与拖放保护、位置不丢失", () => AutoHide(preview));
            step("桌面收纳：贴边窄条贴边直角、悬停高亮、划过不展开及同边多个不重叠", () => EdgeTabs(preview));
            step("桌面收纳：关闭自动贴边后真实拖动到四边、鼠标移出隐藏及移入展开", () => ManualEdgeAutoHide(preview));
            step("桌面收纳：默认不创建、启用延后显示、关闭后释放窗口", HostLifecycle);
            step("桌面收纳：宿主为每个收纳窗逐个建窗、互不遮挡、删除即关窗", HostMultiple);
        }
        finally
        {
            Dragging.SetValue(null, null);
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
        old.DesktopShelf.Boxes = [new() { Id = "../not-a-guid", Name = " \n\t ",
            Items = [new() { Name = " 离线文件 ", Path = Path.Combine(Path.GetTempPath(), "offline.txt") }, new() { Path = "relative.txt" }, null!],
            Width = double.PositiveInfinity, Height = -9, Sort = (ShelfSort)500, Edge = (ShelfEdge)500 }, null!];
        old.DesktopShelf.BackgroundOpacity = double.NaN;
        old.DesktopShelf.CornerRadius = 1000;
        old.DesktopShelf.IconSize = -1;
        old.DesktopShelf.Theme = (ShelfTheme)500;
        SettingsService.Normalize(old);
        var s = old.DesktopShelf;
        Check(s.Boxes.Count == 1 && Guid.TryParseExact(s.Boxes[0].Id, "N", out _) && s.Boxes[0].Name == DesktopShelfService.DefaultName,
            "收纳窗编号或名称未修复");
        var box = s.Boxes[0];
        Check(box.Width == 280 && box.Height == 160 && s.BackgroundOpacity == 0.92 && s.CornerRadius == 24 && s.IconSize == 24, "尺寸/透明度未归一化");
        Check(s.Theme == ShelfTheme.Dark && box.Sort == ShelfSort.Manual && box.Edge == ShelfEdge.Right, "非法枚举未修复");
        Check(box.Items.Count == 1 && box.Items[0].Name == "离线文件", "离线文件丢失或接受了非法路径");
        s.Enabled = true; box.Left = -600; box.Top = 80; box.Sort = ShelfSort.MostUsed; s.Theme = ShelfTheme.Sky;
        DesktopShelfService.AddBox(s).Name = "工作";
        old.QuickLaunchEnabled = true;
        var json = JsonSerializer.Serialize(old, SettingsJson.Default.AppSettings);
        var copy = JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings)!;
        SettingsService.Normalize(copy);
        Check(JsonSerializer.Serialize(copy, SettingsJson.Default.AppSettings) == json, "配置往返不稳定");
        Check(copy.QuickLaunchItems.Count == 0 && copy.DesktopShelf.Enabled, "收纳配置影响了快速启动");
        Check(copy.QuickLaunchEnabled && copy.DesktopShelf.Theme == ShelfTheme.Sky, "新的默认值覆盖了已保存的开关或配色");
        Check(copy.DesktopShelf.Boxes.Count == 2 && copy.DesktopShelf.Boxes[1].Name == "工作" && copy.DesktopShelf.Boxes[0].Left == -600
            && copy.DesktopShelf.Boxes[0].Id == box.Id, "多个收纳窗往返后丢失名称、编号或位置");
        var missing = JsonSerializer.Deserialize("{\"desktopShelf\":null}", SettingsJson.Default.AppSettings)!;
        SettingsService.Normalize(missing);
        Check(!missing.DesktopShelf.Enabled && !missing.QuickLaunchEnabled && missing.DesktopShelf.Theme == ShelfTheme.Dark
            && missing.DesktopShelf.Boxes.Count == 1 && missing.DesktopShelf.Boxes[0].Items.Count == 0, "空配置未修复");
        Check(DesktopShelfService.CleanName(" 工作\t资料\n ") == "工作 资料"
            && DesktopShelfService.CleanName(new string('名', 40)).Length == DesktopShelfBox.MaxNameLength, "名称没有清理成单行或没有截断");
        Check(new StringInfo(DesktopShelfService.CleanName(string.Concat(Enumerable.Repeat("📁", 30)))).LengthInTextElements == DesktopShelfBox.MaxNameLength,
            "截断名称时拆开了表情");
    }

    static void Migration()
    {
        // 1.9.7 只有一个收纳窗：项目、尺寸、位置、停靠边和排序都直接写在 desktopShelf 这一层。
        var stored = Path.Combine(Path.GetTempPath(), "desktop-shelf", "0123456789abcdef0123456789abcdef", "files", "旧文件.txt");
        var source = Path.Combine(Path.GetTempPath(), "Desktop", "旧文件.txt");
        string Legacy(JsonNode left) => new JsonObject
        {
            ["version"] = 13,
            ["desktopShelf"] = new JsonObject
            {
                ["enabled"] = true, ["snapToEdge"] = false, ["autoHide"] = true, ["theme"] = "Sky", ["sort"] = "NameDescending",
                ["cornerRadius"] = 8, ["backgroundOpacity"] = 0.8, ["iconSize"] = 40,
                ["width"] = 320, ["height"] = 420, ["left"] = left, ["top"] = 120, ["edge"] = "Left",
                ["items"] = new JsonArray(new JsonObject { ["name"] = "旧文件", ["path"] = stored, ["launchCount"] = 3,
                    ["storageId"] = "0123456789abcdef0123456789abcdef", ["originalPath"] = source }),
            },
        }.ToJsonString();
        var s = JsonSerializer.Deserialize(Legacy(-500), SettingsJson.Default.AppSettings)!;
        Check(SettingsService.Migrate(s) && s.Version == AppSettings.CurrentVersion, "旧收纳配置没有触发迁移落盘");
        SettingsService.Normalize(s);
        var shelf = s.DesktopShelf;
        Check(shelf.Boxes.Count == 1, "迁移后收纳窗数量错误");
        var box = shelf.Boxes[0];
        Check(box.Name == DesktopShelfService.DefaultName && box.Sort == ShelfSort.NameDescending && box.Width == 320 && box.Height == 420
            && box.Left == -500 && box.Top == 120 && box.Edge == ShelfEdge.Left, "旧窗口的位置、尺寸或排序没有带过来");
        Check(box.Items.Count == 1 && box.Items[0].StorageId == "0123456789abcdef0123456789abcdef" && box.Items[0].OriginalPath == source
            && box.Items[0].LaunchCount == 3, "旧收纳项或还原记录丢失");
        Check(shelf.Enabled && !shelf.SnapToEdge && shelf.Theme == ShelfTheme.Sky && shelf.CornerRadius == 8 && shelf.BackgroundOpacity == 0.8
            && shelf.IconSize == 40, "迁移改动了全局外观");
        var json = JsonSerializer.Serialize(s, SettingsJson.Default.AppSettings);
        using (var document = JsonDocument.Parse(json))
        {
            var node = document.RootElement.GetProperty("desktopShelf");
            foreach (var key in new[] { "items", "width", "height", "left", "top", "edge", "sort" })
                Check(!node.TryGetProperty(key, out _), "迁移后仍写出旧字段：" + key);
        }
        var unplaced = JsonSerializer.Deserialize(Legacy("NaN"), SettingsJson.Default.AppSettings)!;
        SettingsService.Normalize(unplaced);
        Check(double.IsNaN(unplaced.DesktopShelf.Boxes[0].Left) && double.IsNaN(unplaced.DesktopShelf.Boxes[0].Top), "旧配置的未定位窗口被放到了固定坐标");
        // 降级后又升级：新结构为准，旧列表里多出的项目补进第一个收纳窗，已有的不重复。
        var mixed = JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings)!;
        DesktopShelfService.AddBox(mixed.DesktopShelf);
        mixed.DesktopShelf.LegacyItems = [new() { Name = "降级期间新增", Path = Path.Combine(Path.GetTempPath(), "added.txt") },
            new() { Name = "旧文件", Path = stored, StorageId = "0123456789abcdef0123456789abcdef", OriginalPath = source }];
        mixed.DesktopShelf.LegacyWidth = 999;
        SettingsService.Normalize(mixed);
        var boxes = mixed.DesktopShelf.Boxes;
        Check(boxes.Count == 2 && boxes[0].Items.Count == 2 && boxes[0].Width == 320 && boxes[1].Items.Count == 0
            && mixed.DesktopShelf.LegacyItems is null && mixed.DesktopShelf.LegacyWidth is null, "新旧字段并存时项目重复、丢失或覆盖了新位置");
    }

    static void Ordering()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new DesktopShelfEntry { Name = "Alpha", Path = @"D:\Alpha.txt", AddedAt = now.AddDays(-1), LaunchCount = 4, LastLaunched = now };
        var b = new DesktopShelfEntry { Name = "Beta", Path = @"D:\Beta.exe", AddedAt = now, LaunchCount = 7, LastLaunched = now.AddDays(-1) };
        var c = new DesktopShelfEntry { Name = "Gamma", Path = @"D:\Gamma", IsFolder = true };
        var s = new DesktopShelfBox { Items = [c, b, a] };
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
            foreach (var edge in Edges)
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

    static void MultipleBoxes()
    {
        var s = new DesktopShelfSettings();
        DesktopShelfService.Normalize(s);
        Check(s.Boxes.Count == 1 && s.Boxes[0].Name == DesktopShelfService.DefaultName, "缺省不是恰好一个收纳窗");
        var first = s.Boxes[0];
        var a = new DesktopShelfEntry { Name = "A", Path = @"D:\A.txt" };
        var b = new DesktopShelfEntry { Name = "B", Path = @"D:\B.txt" };
        first.Items = [a, b];
        var second = DesktopShelfService.AddBox(s);
        Check(second.Name == "收纳 2" && second.Id != first.Id && double.IsNaN(second.Left) && second.Edge == first.Edge
            && second.Width < first.Width && second.Height < first.Height, "新建收纳窗的名称、编号、停靠边或紧凑尺寸错误");
        while (s.Boxes.Count < DesktopShelfSettings.MaxBoxes) DesktopShelfService.AddBox(s);
        Throws(() => DesktopShelfService.AddBox(s), "超过上限仍能新建收纳窗");
        Check(s.Boxes.Select(x => x.Name).Distinct().Count() == s.Boxes.Count && s.Boxes.Select(x => x.Id).Distinct().Count() == s.Boxes.Count,
            "默认名称或编号重复");
        while (s.Boxes.Count > 2) DesktopShelfService.RemoveBox(s, s.Boxes[^1]);
        Check(DesktopShelfService.MoveEntry(s, b, second) && first.Items.SequenceEqual([a]) && second.Items.SequenceEqual([b]), "移动归属错误");
        Check(!DesktopShelfService.MoveEntry(s, b, second) && !DesktopShelfService.MoveEntry(s, new DesktopShelfEntry { Path = @"D:\X.txt" }, first),
            "无效移动改动了数据");
        var heir = DesktopShelfService.RemoveBox(s, second);
        Check(ReferenceEquals(heir, first) && s.Boxes.Count == 1 && first.Items.SequenceEqual([a, b]), "删除收纳窗没有把项目并入");
        Throws(() => DesktopShelfService.RemoveBox(s, first), "删掉了最后一个收纳窗");
        var third = DesktopShelfService.AddBox(s);
        Check(ReferenceEquals(DesktopShelfService.RemoveBox(s, first), third) && third.Items.SequenceEqual([a, b]), "删除第一个收纳窗后项目去向错误");
        var full = new DesktopShelfSettings { Boxes = [new() { Items = [a] }, new() { Items = Enumerable.Range(0, QuickLaunchService.MaxItems)
            .Select(i => new DesktopShelfEntry { Name = $"F{i}", Path = $@"D:\F{i}.txt" }).ToList() }] };
        Throws(() => DesktopShelfService.MoveEntry(full, a, full.Boxes[1]), "移入已满的收纳窗");
        Throws(() => DesktopShelfService.RemoveBox(full, full.Boxes[0]), "合并后超过上限仍删除");
        Check(full.Boxes[0].Items.SequenceEqual([a]) && full.Boxes.Count == 2, "失败的移动或删除改动了数据");

        var duplicate = new DesktopShelfSettings { Boxes = [new() { Items = [new() { Name = "A", Path = @"D:\A.txt" }] },
            new() { Items = [new() { Name = "A2", Path = @"d:\a.txt" }, new() { Name = "C", Path = @"D:\C.txt" }] }] };
        DesktopShelfService.Normalize(duplicate);
        Check(duplicate.Boxes[0].Items.Count == 1 && duplicate.Boxes[1].Items.Single().Name == "C" && duplicate.Boxes[0].Id != duplicate.Boxes[1].Id,
            "同一路径出现在两个收纳窗里没有去重");
        var many = new DesktopShelfSettings { Boxes = Enumerable.Range(0, DesktopShelfSettings.MaxBoxes + 2)
            .Select(i => new DesktopShelfBox { Items = [new() { Name = $"M{i}", Path = $@"D:\M{i}.txt" }] }).ToList() };
        DesktopShelfService.Normalize(many);
        Check(many.Boxes.Count == DesktopShelfSettings.MaxBoxes && many.Boxes.Sum(x => x.Items.Count) == DesktopShelfSettings.MaxBoxes + 2,
            "超出数量上限的收纳窗丢了项目");

        foreach (var scale in new[] { 1d, 1.5 })
        {
            var work = new Rect(-1920 / scale, 40 / scale, 1920 / scale, 1040 / scale);
            var single = DesktopShelfService.FreeSlot(new Size(280, 340), work, [], ShelfEdge.Right);
            Check(single.Edge == ShelfEdge.Right && single.Position == new Point(work.Right - 280, work.Top + 64), "单个收纳窗的默认位置变了");
            var taken = new List<Rect> { new(single.Position, new Size(280, 340)) };
            var sides = new HashSet<ShelfEdge>();
            // 屏幕放得下时依次排开：贴在停靠边上、互不重叠，右侧满了换到左侧；放满之后才允许错开叠放。
            while (true)
            {
                var slot = DesktopShelfService.FreeSlot(new Size(240, 260), work, taken, ShelfEdge.Right);
                var rect = new Rect(slot.Position, new Size(240, 260));
                Check(work.Contains(rect), "新收纳窗出界");
                if (taken.Any(t => Overlaps(t, rect))) break;
                Check(slot.Edge == ShelfEdge.Right ? rect.Right == work.Right : slot.Edge == ShelfEdge.Left && rect.Left == work.Left,
                    "新收纳窗没有贴在停靠边");
                sides.Add(slot.Edge);
                taken.Add(rect);
                Check(taken.Count < 40, "空位计算没有收敛");
            }
            Check(sides.SetEquals([ShelfEdge.Right, ShelfEdge.Left]) && taken.Count >= 4, "右侧放满后没有换到左侧");
        }

        Check(DesktopShelfService.TabOffset(100, 72, 0, 1000, [(300, 372)], 8) == 100, "不冲突时窄条被挪动");
        Check(DesktopShelfService.TabOffset(100, 72, 0, 1000, [(90, 162)], 8) == 170, "窄条没有挪到最近的空位");
        Check(DesktopShelfService.TabOffset(950, 72, 0, 1000, [(900, 972)], 8) == 820, "靠近屏幕尽头时窄条越界或没有错开");
        var crowd = new List<(double, double)>();
        for (var i = 0; i < 6; i++)
        {
            var start = DesktopShelfService.TabOffset(400, 72, 40, 1080, crowd, 8);
            Check(start >= 40 && start + 72 <= 1080 && crowd.All(o => start + 72 + 8 <= o.Item1 + 0.01 || start >= o.Item2 + 8 - 0.01), "多个窄条叠在一起");
            crowd.Add((start, start + 72));
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
        Box.Items.Clear();
        var window = new DesktopShelfWindow(Box, () => { });
        try
        {
            window.Show(); Await(window.InventoryReady);
            Await(window.AddPathsAsync([file, folder, file, script, Path.Combine(dir, "missing.txt")]));
            Check(Box.Items.Count == 2 && Box.Items.All(i => i.IsStored && i.AddedAt is not null), "批量移入或记录错误");
            Check(!File.Exists(file) && !Directory.Exists(folder), "拖入仍然只是入口，原文件没有移走");
            var storedFile = Box.Items.Single(i => !i.IsFolder);
            var storedFolder = Box.Items.Single(i => i.IsFolder);
            Check(File.ReadAllText(storedFile.Path) == "original" && File.ReadAllText(Path.Combine(storedFolder.Path, "inside.txt")) == "folder content",
                "收纳文件内容错误");
            Await(window.AddPathsAsync([file]));
            Check(Box.Items.Count == 2 && File.Exists(script), "重复添加或不支持类型破坏原文件");
            Await(window.RemoveEntryAsync(storedFile)); Await(window.RemoveEntryAsync(storedFolder));
            Check(Box.Items.Count == 0 && File.ReadAllText(file) == "original" && File.ReadAllText(Path.Combine(folder, "inside.txt")) == "folder content",
                "解除后未还原文件/目录");
        }
        finally { window.Close(); RestoreTestFiles(dir); }
        Check(SettingsService.Instance.Current.QuickLaunchItems.Count == 0, "添加收纳污染了快捷启动");
    });

    static void Layout(string? preview) => InTemp(dir =>
    {
        var file = Path.Combine(dir, "项目进度与中英文资料 Project notes.txt");
        File.WriteAllText(file, "sample");
        Box.Items = [new() { Name = "项目进度与中英文资料 Project notes 长文件名", Path = file },
            new() { Name = "Project files", Path = dir, IsFolder = true },
            new() { Name = "启动程序", Path = Environment.ProcessPath! },
            new() { Name = "离线文档", Path = Path.Combine(dir, "gone.pdf") }];
        foreach (var theme in Enum.GetValues<ShelfTheme>())
        foreach (var size in new[] { (200d, 160d, 48), (280d, 340d, 32), (800d, 700d, 24) })
        {
            S.Theme = theme; Box.Width = size.Item1; Box.Height = size.Item2; S.IconSize = size.Item3;
            S.CornerRadius = theme == ShelfTheme.Light ? 0 : 12;
            S.BackgroundOpacity = theme == ShelfTheme.Dark ? 0.7 : 0.92;
            var window = new DesktopShelfWindow(Box, () => { });
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
        Box.Items.Clear(); Box.Width = 280; Box.Height = 240; S.IconSize = 32;
        var empty = new DesktopShelfWindow(Box, () => { });
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
        Box.Items = []; Box.Width = 280; Box.Height = 260; S.IconSize = 32; Box.Sort = ShelfSort.NameDescending;
        var window = new DesktopShelfWindow(Box, () => { });
        try
        {
            window.Show(); Pump();
            var drop = DropArgs(new DataObject(DataFormats.FileDrop, new[] { a, b }), window);
            drop.RoutedEvent = DragDrop.PreviewDropEvent;
            window.RaiseEvent(drop);
            Wait(() => Box.Items.Count == 2, "窗口拖入未添加"); Pump();
            var entries = Descendants<Button>(window).Where(button => button.Name == "ShelfEntry").ToArray();
            Check(!File.Exists(a) && !File.Exists(b), "Drop 事件没有移动原文件");
            var source = (DesktopShelfEntry)entries[0].Tag;
            var data = new DataObject("FlashTrans.DesktopShelf.Entry", source);
            // 外来同名格式不能改变列表；只有本进程正在拖的那一项才允许交换。
            drop = DropArgs(data, entries[1]); drop.RoutedEvent = DragDrop.PreviewDropEvent;
            entries[1].RaiseEvent(drop);
            Check(Box.Sort == ShelfSort.NameDescending, "接受了外来换位");
            Dragging.SetValue(null, source);
            drop = DropArgs(data, entries[1]); drop.RoutedEvent = DragDrop.PreviewDropEvent;
            entries[1].RaiseEvent(drop); Pump();
            Dragging.SetValue(null, null);
            Check(Box.Sort == ShelfSort.Manual && Box.Items[0].OriginalPath == a && Box.Items[1].OriginalPath == b, "拖放没有交换显示位置");
            Check(Box.Items.All(i => i.LaunchCount == 0), "拖放触发了文件打开");
            var sort = Descendants<Button>(window).Single(button => button.Name == "ShelfSort");
            sort.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var option = sort.ContextMenu!.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "文件类型"));
            option.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); sort.ContextMenu.IsOpen = false;
            Check(Box.Sort == ShelfSort.Type, "排序按钮未生效");
            var remove = Descendants<Button>(window).First(button => button.Name == "ShelfRemove");
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Wait(() => Box.Items.Count == 1, "一键解除没有完成");
            Check(File.ReadAllText(a) == "a" && !File.Exists(b) && File.ReadAllText(Box.Items.Single().Path) == "b", "一键解除没有正确还原文件");
        }
        finally { Dragging.SetValue(null, null); window.Close(); RestoreTestFiles(dir); }
    });

    static void CrossWindowDrag() => InTemp(dir =>
    {
        var file = Path.Combine(dir, "跨收纳窗.txt"); File.WriteAllText(file, "cross");
        S.Boxes = [Box]; Box.Items.Clear(); Box.Width = 280; Box.Height = 260; Box.Sort = ShelfSort.Manual;
        var other = DesktopShelfService.AddBox(S);
        other.Name = "工作";
        var source = new DesktopShelfWindow(Box, () => { });
        var target = new DesktopShelfWindow(other, () => { });
        try
        {
            source.Show(); target.Show(); Pump(); Await(source.InventoryReady); Await(target.InventoryReady);
            Await(source.AddPathsAsync([file]));
            var entry = Box.Items.Single();
            var stored = entry.Path;
            var data = new DataObject("FlashTrans.DesktopShelf.Entry", entry);
            var drop = DropArgs(data, target); drop.RoutedEvent = DragDrop.PreviewDropEvent;
            target.RaiseEvent(drop);
            Check(Box.Items.Count == 1 && other.Items.Count == 0, "接受了不是本进程正在拖的同名格式数据");
            Dragging.SetValue(null, entry);
            S.AutoHide = true;
            target.CollapseToEdge(animate: false);
            Check(target.IsEdgeCollapsed, "目标收纳窗没有收起，无法验证拖入展开");
            var over = DropArgs(data, target); over.RoutedEvent = DragDrop.PreviewDragOverEvent;
            target.RaiseEvent(over);
            S.AutoHide = false;
            Check(over.Effects == DragDropEffects.Move && !target.IsEdgeCollapsed, "拖着图标经过另一个收纳窗时不接受或没有展开");
            over = DropArgs(data, source); over.RoutedEvent = DragDrop.PreviewDragOverEvent;
            source.RaiseEvent(over);
            Check(over.Effects == DragDropEffects.None, "拖回原收纳窗空白处仍显示可放下");
            drop = DropArgs(data, target); drop.RoutedEvent = DragDrop.PreviewDropEvent;
            target.RaiseEvent(drop); Pump();
            Dragging.SetValue(null, null);
            Check(Box.Items.Count == 0 && other.Items.SequenceEqual([entry]) && entry.Path == stored && File.ReadAllText(stored) == "cross"
                && !File.Exists(file), "跨收纳窗移动改动了文件或归属错误");
            Check(Descendants<Button>(target).Count(b => b.Name == "ShelfEntry") == 1, "目标收纳窗没有显示移入的图标");
            SettingsService.Instance.Load();
            Check(S.Boxes.Count == 2 && S.Boxes[1].Items.Single().StorageId == entry.StorageId && S.Boxes[0].Items.Count == 0, "跨收纳窗移动没有保存");
        }
        finally
        {
            Dragging.SetValue(null, null);
            S.AutoHide = false;
            source.Close(); target.Close();
            RestoreTestFiles(dir);
            S.Boxes = [Box];
        }
        Check(File.ReadAllText(file) == "cross", "还原后文件内容错误");
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
        Box.Items.Clear();
        var window = new DesktopShelfWindow(Box, () => { });
        try
        {
            window.Show(); Pump(); Await(window.AddPathsAsync([link]));
            var start = Stopwatch.StartNew();
            var task = window.LaunchEntryAsync(Box.Items.Single());
            var returned = start.Elapsed.TotalMilliseconds;
            var responsive = false;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => responsive = true);
            Await(task); Wait(() => File.Exists(marker), "快捷方式未实际启动");
            Check(responsive && Box.Items[0].LaunchCount == 1 && Box.Items[0].LastLaunched is not null && window.IsVisible,
                "启动阻塞 UI、未记录使用次数或关闭了收纳窗");
            Console.WriteLine($"       发起打开返回 {returned:F1}ms，隔离进程完成 {start.Elapsed.TotalMilliseconds:F1}ms");
            Check(!File.Exists(link) && File.Exists(Box.Items.Single().Path), "快捷方式仍留在原位置");
            Await(window.LaunchEntryAsync(new DesktopShelfEntry { Name = "missing", Path = Path.Combine(dir, "missing.exe") }));
            Check(Descendants<TextBlock>(window).Any(t => t.Text.StartsWith("打开失败：")), "失败没有可见提示");
        }
        finally { window.Close(); RestoreTestFiles(dir); }
    });

    static void SettingsUi(string? preview)
    {
        Box.Items.Clear();
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

    static void SettingsMoves() => InTemp(dir =>
    {
        var file = Path.Combine(dir, "设置页移动.txt"); File.WriteAllText(file, "settings move");
        Box.Items.Clear();
        var window = new SettingsWindow(new AppHost());
        try
        {
            window.SelectTab("desktopshelf"); window.Show(); Pump(); Await(window.ShelfInventoryReady);
            Await(window.AddShelfPathsAsync([file]));
            Check(Box.Items.Count == 1 && !File.Exists(file) && File.ReadAllText(Box.Items[0].Path) == "settings move", "设置页未移动文件");
            var remove = Descendants<Button>(window).Single(b => Equals(b.ToolTip, "解除收纳并还原到原位置"));
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => Box.Items.Count == 0, "设置页解除未完成");
            Check(File.ReadAllText(file) == "settings move", "设置页解除未还原文件");
        }
        finally { window.Close(); RestoreTestFiles(dir); }
    });

    static void SettingsBoxes() => InTemp(dir =>
    {
        S.Boxes = [Box]; Box.Items.Clear(); Box.Name = DesktopShelfService.DefaultName; Box.Width = 280;
        var file = Path.Combine(dir, "第二个收纳窗.txt"); File.WriteAllText(file, "second");
        var window = new SettingsWindow(new AppHost()) { Width = 620, Height = 560, ShowActivated = false };
        try
        {
            window.SelectTab("desktopshelf"); window.Show(); Pump(); Await(window.ShelfInventoryReady); Pump();
            Check(!Descendants<ComboBox>(window).Any(c => c.Name == "ShelfPicker")
                && !Descendants<Button>(window).Single(b => b.Name == "ShelfDelete").IsEnabled, "只有一个收纳窗时仍可切换或删除");
            Click(Descendants<Button>(window).Single(b => b.Name == "ShelfCreate")); Pump();
            Check(S.Boxes.Count == 2, "设置页没有新建收纳窗");
            var picker = Descendants<ComboBox>(window).Single(c => c.Name == "ShelfPicker");
            Check(picker.SelectedIndex == 1 && Descendants<TextBox>(window).Single(t => t.Name == "ShelfName").Text == S.Boxes[1].Name,
                "新建后没有切到新收纳窗");
            var name = Descendants<TextBox>(window).Single(t => t.Name == "ShelfName");
            name.Text = "  工作\t资料  "; name.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent)); Pump();
            Check(S.Boxes[1].Name == "工作 资料" && Box.Name == DesktopShelfService.DefaultName, "改名没有生效或改错了收纳窗");
            Descendants<Slider>(window).Single(s => s.Name == "ShelfWidth").Value = 400;
            Check(S.Boxes[1].Width == 400 && Box.Width == 280, "尺寸改到了别的收纳窗");
            Await(window.AddShelfPathsAsync([file])); Pump();
            Check(S.Boxes[1].Items.Count == 1 && Box.Items.Count == 0, "设置页移入没有进入正在设置的收纳窗");
            Descendants<ComboBox>(window).Single(c => c.Name == "ShelfPicker").SelectedIndex = 0; Pump(); Pump();
            Check(Descendants<TextBox>(window).Single(t => t.Name == "ShelfName").Text == DesktopShelfService.DefaultName,
                "切换收纳窗后表单没有更新");
            Descendants<ComboBox>(window).Single(c => c.Name == "ShelfPicker").SelectedIndex = 1; Pump(); Pump();
            // 删掉空收纳窗不弹确认；有项目的那个在窗口菜单测试里确认并入。
            S.Boxes[1].Items.ForEach(i => Box.Items.Add(i)); S.Boxes[1].Items.Clear();
            Click(Descendants<Button>(window).Single(b => b.Name == "ShelfDelete")); Pump();
            Check(S.Boxes.Count == 1 && Box.Name == DesktopShelfService.DefaultName && Box.Items.Count == 1, "删除收纳窗失败或丢了项目");
            SettingsService.Instance.Load();
            Check(S.Boxes.Count == 1 && Box.Items.Count == 1, "删除没有落盘");
        }
        finally { window.Close(); RestoreTestFiles(dir); }
        Check(File.ReadAllText(file) == "second", "还原后文件内容错误");
    });

    static void RenameAndMenu()
    {
        S.Boxes = [Box]; Box.Items.Clear(); Box.Name = DesktopShelfService.DefaultName;
        var window = new DesktopShelfWindow(Box, () => { });
        try
        {
            window.Show(); Pump();
            var menuButton = Descendants<Button>(window).Single(b => b.Name == "ShelfSettings");
            Click(menuButton); Pump();
            var open = OpenMenuItems();
            Check(open.Any(i => Equals(i.Header, "新建收纳窗") && i.IsEnabled) && open.Any(i => Equals(i.Header, "删除此收纳窗…") && !i.IsEnabled)
                && open.Any(i => Equals(i.Header, "收纳设置…")), "收纳窗菜单缺项或只有一个时仍可删除");
            CloseMenus();
            var header = Descendants<Grid>(window).Single(g => g.Name == "ShelfHeader");
            var title = Descendants<TextBlock>(header).First(t => t.Text == DesktopShelfService.DefaultName);
            // MouseLeftButtonDown 是直接路由事件，真实点击时由 MouseDown 沿路逐个重发；这里直接发给标题栏。
            header.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent }.WithClickCount(2));
            Pump();
            var edit = Descendants<TextBox>(window).Single(t => t.Name == "ShelfRename");
            Wait(() => edit.IsVisible, "双击标题没有进入改名");
            Check(!window.TryAutoHide(), "改名时自动收起");
            edit.Text = "  素材 \n 收集  ";
            edit.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(edit)!, Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Pump();
            Check(Box.Name == "素材 收集" && !edit.IsVisible && title.Text == "素材 收集" && window.Title.EndsWith("素材 收集"), "改名没有保存或界面没有更新");
            window.BeginRename(); Pump();
            edit.Text = "不要";
            edit.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(edit)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Check(Box.Name == "素材 收集", "Esc 取消改名仍保存了");
            window.BeginRename(); Pump();
            edit.Text = "   ";
            window.EndRename(commit: true);
            Check(Box.Name == "素材 收集", "空名称覆盖了原名");
        }
        finally { CloseMenus(); window.Close(); Box.Name = DesktopShelfService.DefaultName; }
    }

    static void AutoHide(string? preview)
    {
        S.Boxes = [Box];
        Box.Items.Clear(); Box.Width = 280; Box.Height = 300; S.CornerRadius = 12; S.IconSize = 32;
        S.SnapToEdge = true; S.AutoHide = true; S.Theme = ShelfTheme.Sky;
        Win32.GetCursorPos(out var cursor);
        var window = new DesktopShelfWindow(Box, () => { });
        try
        {
            window.Show(); Pump();
            foreach (var edge in Edges)
            {
                window.ExpandFromEdge();
                window.FitToScreen(edge); window.PersistGeometry();
                var full = new Rect(window.Left, window.Top, window.Width, window.Height);
                window.CollapseToEdge(); Pump();
                Check(window.IsEdgeCollapsed && window.Topmost, "未收起为可呼出的窄边");
                Check(edge is ShelfEdge.Left or ShelfEdge.Right
                    ? window.Width == DesktopShelfWindow.TabThickness && window.Height == DesktopShelfWindow.TabLength
                    : window.Height == DesktopShelfWindow.TabThickness && window.Width == DesktopShelfWindow.TabLength, "隐藏窗口仍占大块桌面");
                window.PersistGeometry();
                Check(Win32.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect), "未取得原生窗口尺寸");
                var dpi = VisualTreeHelper.GetDpi(window);
                Check(Math.Abs(rect.Right - rect.Left - window.Width * dpi.DpiScaleX) <= 1
                    && Math.Abs(rect.Bottom - rect.Top - window.Height * dpi.DpiScaleY) <= 1,
                    "WPF 虽已缩小，原生窗口仍挡住桌面");
                Check(Box.Width == full.Width && Box.Height == full.Height && Box.Left == full.Left && Box.Top == full.Top,
                    "收起状态覆盖了正常窗口位置或尺寸");
                if (preview is not null) { Settle(); Snapshot(window, Path.Combine(preview, $"shelf-hidden-{edge}.png")); }
                var foreground = Win32.GetForegroundWindow();
                window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                Wait(() => !window.IsEdgeCollapsed, "悬停窄条没有展开");
                Check(new Rect(window.Left, window.Top, window.Width, window.Height) == full, "移入未恢复原位置/尺寸");
                Check(Win32.GetForegroundWindow() == foreground, "悬停展开抢走了前台焦点");

                var sort = Descendants<Button>(window).Single(b => b.Name == "ShelfSort");
                sort.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Check(!window.TryAutoHide() && !window.IsEdgeCollapsed, "排序菜单打开时误收起");
                sort.ContextMenu!.IsOpen = false;
                foreach (var field in new[] { "_moving", "_resizing", "_dragging", "_dialogOpen", "_adding", "_renaming" })
                {
                    var info = typeof(DesktopShelfWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!;
                    info.SetValue(window, true);
                    Check(!window.TryAutoHide(), "交互中误收起：" + field);
                    info.SetValue(window, false);
                }
                window.CollapseToEdge();
                var data = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(Environment.CurrentDirectory, "README.md") });
                var over = DropArgs(data, window); over.RoutedEvent = DragDrop.PreviewDragOverEvent;
                window.RaiseEvent(over);
                Check(!window.IsEdgeCollapsed && over.Effects == DragDropEffects.Move, "拖文件到窄边未展开");
                window.CollapseToEdge();
                S.AutoHide = false; window.ApplySettings();
                Check(!window.IsEdgeCollapsed && !window.TryAutoHide(), "关闭自动隐藏后没有展开或仍收起");
                S.AutoHide = true;
            }
            window.ExpandFromEdge(); window.FitToScreen(ShelfEdge.Left);
            // 定时收起要求光标不在收纳窗上；用户的鼠标可能正停在左下角，先挪到屏幕中央，结束后放回。
            var work = ScreenHelper.WorkAreaOf(window);
            var scale = VisualTreeHelper.GetDpi(window);
            Check(Win32.SetCursorPos((int)((work.Left + work.Width / 2) * scale.DpiScaleX), (int)((work.Top + work.Height / 2) * scale.DpiScaleY)),
                "无法移开鼠标");
            Pump();
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
        finally { window.Close(); S.AutoHide = false; S.SnapToEdge = true; Win32.SetCursorPos(cursor.X, cursor.Y); Pump(); }
        var reopened = new DesktopShelfWindow(Box, () => { });
        try
        {
            reopened.Show(); Pump();
            Check(reopened.Width == Box.Width && reopened.Height == Box.Height && reopened.Left == Box.Left && reopened.Top == Box.Top,
                "关闭/重开后丢失收起前的位置和大小");
        }
        finally { reopened.Close(); }
    }

    static void EdgeTabs(string? preview)
    {
        S.Boxes = [Box];
        Box.Items.Clear(); Box.Width = 280; Box.Height = 300;
        S.SnapToEdge = true; S.AutoHide = true; S.BackgroundOpacity = 0.5;
        var hover = typeof(DesktopShelfWindow).GetMethod("HoverTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            foreach (var theme in Enum.GetValues<ShelfTheme>())
            {
                S.Theme = theme;
                var window = new DesktopShelfWindow(Box, () => { });
                try
                {
                    window.Show(); Pump();
                    var accent = ((SolidColorBrush)window.FindResource("Accent")).Color;
                    foreach (var edge in Edges)
                    {
                        window.ExpandFromEdge(); window.FitToScreen(edge); window.PersistGeometry();
                        window.CollapseToEdge(animate: false); window.UpdateLayout();
                        var tab = Descendants<Border>(window).Single(b => b.Name == "ShelfEdgeTab");
                        var (screenCorners, inner) = edge switch
                        {
                            ShelfEdge.Left => (tab.CornerRadius.TopLeft + tab.CornerRadius.BottomLeft + tab.BorderThickness.Left,
                                tab.CornerRadius.TopRight + tab.CornerRadius.BottomRight),
                            ShelfEdge.Right => (tab.CornerRadius.TopRight + tab.CornerRadius.BottomRight + tab.BorderThickness.Right,
                                tab.CornerRadius.TopLeft + tab.CornerRadius.BottomLeft),
                            ShelfEdge.Top => (tab.CornerRadius.TopLeft + tab.CornerRadius.TopRight + tab.BorderThickness.Top,
                                tab.CornerRadius.BottomLeft + tab.CornerRadius.BottomRight),
                            _ => (tab.CornerRadius.BottomLeft + tab.CornerRadius.BottomRight + tab.BorderThickness.Bottom,
                                tab.CornerRadius.TopLeft + tab.CornerRadius.TopRight),
                        };
                        Check(screenCorners == 0 && inner > 0, "窄条贴屏幕的一侧仍有圆角或描边：" + edge);
                        var pixels = Pixels(window, out var width, out var height);
                        // 贴屏幕边的两个角必须实心；内侧两个角是圆的、透明；中间底色不跟着背景不透明度变淡。
                        var (outerA, outerB, innerA) = edge switch
                        {
                            ShelfEdge.Left => ((0, 0), (0, height - 1), (width - 1, 0)),
                            ShelfEdge.Right => ((width - 1, 0), (width - 1, height - 1), (0, 0)),
                            ShelfEdge.Top => ((0, 0), (width - 1, 0), (0, height - 1)),
                            _ => ((0, height - 1), (width - 1, height - 1), (0, 0)),
                        };
                        Check(Alpha(pixels, width, outerA) > 200 && Alpha(pixels, width, outerB) > 200, "窄条贴边的角不实心：" + edge);
                        Check(Alpha(pixels, width, innerA) < 80, "窄条内侧没有圆角：" + edge);
                        var middle = edge is ShelfEdge.Left or ShelfEdge.Right ? (width / 2, height / 4) : (width / 4, height / 2);
                        Check(Alpha(pixels, width, middle) >= 225, "窄条底色跟着背景一起变淡，桌面上看不清");
                        hover.Invoke(window, [true]); window.UpdateLayout();
                        var hot = Pixels(window, out _, out _);
                        var color = PixelColor(hot, width, middle);
                        Check(Math.Abs(color.R - accent.R) + Math.Abs(color.G - accent.G) + Math.Abs(color.B - accent.B) < 40, "悬停时窄条没有高亮");
                        hover.Invoke(window, [false]);
                        Check(window.IsEdgeCollapsed, "悬停刚开始就展开了");
                    }
                    // 划过窄条（还没停稳就离开）不展开。
                    window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                    window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                    Settle(350);
                    Check(window.IsEdgeCollapsed, "鼠标只是划过窄条也展开了");
                    var tabBorder = Descendants<Border>(window).Single(b => b.Name == "ShelfEdgeTab");
                    tabBorder.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                    Check(!window.IsEdgeCollapsed, "点击窄条没有立即展开");
                }
                finally { window.Close(); }
            }

            // 三个收纳窗都停在右边且展开时互相重叠，收起后的窄条必须错开、都在屏幕内。
            S.Theme = ShelfTheme.Dark; S.BackgroundOpacity = 0.92;
            S.Boxes = [Box];
            DesktopShelfService.AddBox(S); DesktopShelfService.AddBox(S);
            var windows = S.Boxes.Select(b => new DesktopShelfWindow(b, () => { })).ToList();
            try
            {
                foreach (var w in windows) w.Show();
                Pump();
                var work = ScreenHelper.WorkAreaOf(windows[0]);
                for (var i = 0; i < windows.Count; i++)
                {
                    var w = windows[i];
                    w.ExpandFromEdge();
                    w.Left = work.Right - w.Width; w.Top = work.Top + 120 + i * 30;
                    w.FitToScreen(ShelfEdge.Right); w.PersistGeometry();
                }
                foreach (var w in windows) w.CollapseToEdge(animate: false);
                Pump();
                var tabs = windows.Select(w => new Rect(w.Left, w.Top, w.Width, w.Height)).ToList();
                foreach (var tab in tabs) Check(tab.Right == work.Right && tab.Top >= work.Top && tab.Bottom <= work.Bottom, "窄条不在屏幕边缘上");
                for (var i = 0; i < tabs.Count; i++)
                    for (var j = i + 1; j < tabs.Count; j++)
                        Check(tabs[i].Bottom + 7.9 <= tabs[j].Top || tabs[j].Bottom + 7.9 <= tabs[i].Top, "同一条边上的窄条叠在一起");
                windows[1].ExpandFromEdge(); windows[1].CollapseToEdge(animate: false);
                var again = new Rect(windows[1].Left, windows[1].Top, windows[1].Width, windows[1].Height);
                Check(tabs.Where((_, i) => i != 1).All(t => t.Bottom + 7.9 <= again.Top || again.Bottom + 7.9 <= t.Top), "重新收起后窄条压到了邻居");
                if (preview is not null) SaveStack(windows, work, Path.Combine(preview, "shelf-tabs-stacked.png"));
                Check(S.Boxes.All(b => b.Edge == ShelfEdge.Right) && S.Boxes.Select(b => b.Top).Distinct().Count() == 3,
                    "错开窄条改动了收纳窗展开后的位置");
            }
            finally { foreach (var w in windows) w.Close(); }
        }
        finally { S.Boxes = [Box]; S.AutoHide = false; S.BackgroundOpacity = 0.92; }
    }

    static void ManualEdgeAutoHide(string? preview)
    {
        var shelf = new DesktopShelfSettings { SnapToEdge = false, Boxes = [new() { Width = 280, Height = 300 }] };
        DesktopShelfService.Normalize(shelf);
        SettingsService.Instance.Current.DesktopShelf = shelf;
        Win32.GetCursorPos(out var cursor);
        var window = new DesktopShelfWindow(Box, () => { });
        try
        {
            window.Show(); Await(window.InventoryReady); window.Activate(); Pump();
            var work = ScreenHelper.WorkAreaOf(window);
            var center = new Point(work.Left + (work.Width - window.Width) / 2, work.Top + (work.Height - window.Height) / 2);
            foreach (var edge in Edges)
            {
                window.ExpandFromEdge();
                window.Left = center.X; window.Top = center.Y;
                window.FitToScreen(); window.PersistGeometry(); window.Activate(); Pump();
                Check(Box.Edge == ShelfEdge.None && !window.TryAutoHide() && !window.Topmost, "屏幕中间误隐藏或无故置顶");
                var destination = center;
                if (edge == ShelfEdge.Left) destination.X = work.Left + 4;
                if (edge == ShelfEdge.Right) destination.X = work.Right - window.Width - 4;
                if (edge == ShelfEdge.Top) destination.Y = work.Top + 4;
                if (edge == ShelfEdge.Bottom) destination.Y = work.Bottom - window.Height - 4;
                DragShelf(window, destination);
                Wait(() => Box.Edge == edge, "真实拖动后未识别停靠边：" + edge);
                Check((new Point(window.Left, window.Top) - destination).Length < 1, "关闭吸附后窗口仍被强制移动到边缘");
                var expanded = new Rect(window.Left, window.Top, window.Width, window.Height);
                if (preview is not null && edge == ShelfEdge.Left) Snapshot(window, Path.Combine(preview, "shelf-default-dark-manual.png"));

                var outside = window.PointToScreen(new Point(work.Left + work.Width / 2 - window.Left, work.Top + work.Height / 2 - window.Top));
                Check(Win32.SetCursorPos((int)outside.X, (int)outside.Y), "无法移开鼠标");
                Wait(() => !window.IsMouseOver, "鼠标未离开收纳窗");
                Wait(() => window.IsEdgeCollapsed, "关闭自动贴边后鼠标移出仍不隐藏：" + edge);
                Check(Box.Left == expanded.Left && Box.Top == expanded.Top && Box.Width == expanded.Width && Box.Height == expanded.Height,
                    "手动靠边收起后覆盖了原窗口位置");
                Check(edge switch { ShelfEdge.Left => window.Left == work.Left, ShelfEdge.Right => window.Left + window.Width == work.Right,
                    ShelfEdge.Top => window.Top == work.Top, _ => window.Top + window.Height == work.Bottom }, "窄条没有落在屏幕边缘");
                if (preview is not null) { Settle(); Snapshot(window, Path.Combine(preview, $"shelf-manual-hidden-{edge}.png")); }
                // 光标停在窄条最外侧那一像素：展开后它落在收纳窗与屏幕边缘的缝里，不能来回闪。
                var rim = edge switch
                {
                    ShelfEdge.Left => new Point(0.5, window.Height / 2), ShelfEdge.Right => new Point(window.Width - 0.5, window.Height / 2),
                    ShelfEdge.Top => new Point(window.Width / 2, 0.5), _ => new Point(window.Width / 2, window.Height - 0.5),
                };
                var tab = window.PointToScreen(rim);
                Check(Win32.SetCursorPos((int)tab.X, (int)tab.Y), "无法移入窄边");
                Wait(() => !window.IsEdgeCollapsed, "真实鼠标移入窄边未展开：" + edge);
                Check(new Rect(window.Left, window.Top, window.Width, window.Height) == expanded, "展开后手动位置或大小丢失");
                Settle(1800);
                Check(!window.IsEdgeCollapsed, "光标停在收纳窗与屏幕边缘的缝里时反复收起：" + edge);
                Check(Win32.SetCursorPos((int)outside.X, (int)outside.Y), "无法移开鼠标");
                Wait(() => window.IsEdgeCollapsed, "从缝里移开后没有收起：" + edge);
                var inside = window.PointToScreen(new Point(window.Width / 2, window.Height / 2));
                Check(Win32.SetCursorPos((int)inside.X, (int)inside.Y), "无法移入窄边");
                Wait(() => !window.IsEdgeCollapsed, "真实鼠标移入窄边未展开：" + edge);
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

    static void HostMultiple()
    {
        SettingsService.Instance.Apply(AppSettings.CreateDefault());
        var host = new AppHost();
        IEnumerable<DesktopShelfWindow> Windows() => Application.Current.Windows.OfType<DesktopShelfWindow>();
        try
        {
            host.Start(startHidden: true, diagnostic: true);
            S.Enabled = true;
            DesktopShelfService.AddBox(S); DesktopShelfService.AddBox(S);
            SettingsService.Instance.Touch();
            Check(!Windows().Any(), "设置更新同步创建窗口，挤占调用路径");
            Wait(() => Windows().Count(w => w.IsVisible) == 3, "没有为每个收纳窗创建窗口");
            Check(Windows().Select(w => w.BoxId).OrderBy(id => id).SequenceEqual(S.Boxes.Select(b => b.Id).OrderBy(id => id)), "窗口与收纳窗没有一一对应");
            Wait(() => S.Boxes.All(b => double.IsFinite(b.Left)), "新收纳窗位置没有记下");
            var rects = S.Boxes.Select(b => new Rect(b.Left, b.Top, b.Width, b.Height)).ToList();
            for (var i = 0; i < rects.Count; i++)
                for (var j = i + 1; j < rects.Count; j++)
                {
                    var overlap = Rect.Intersect(rects[i], rects[j]);
                    Check(overlap.IsEmpty || overlap.Width < 0.5 || overlap.Height < 0.5, "新收纳窗互相压住");
                }
            Wait(() => Windows().All(w => w.IsEdgeCollapsed), "新收纳窗展开后没有按设置收起");
            var tabs = Windows().Select(w => new Rect(w.Left, w.Top, w.Width, w.Height)).ToList();
            for (var i = 0; i < tabs.Count; i++)
                for (var j = i + 1; j < tabs.Count; j++)
                    Check(!tabs[i].IntersectsWith(tabs[j]), "收起后的窄条叠在一起");
            host.ShowDesktopShelf();
            Check(Windows().All(w => !w.IsEdgeCollapsed), "「显示收纳窗」没有展开全部收纳窗");
            var victim = S.Boxes[1];
            DesktopShelfService.RemoveBox(S, victim); SettingsService.Instance.Touch();
            Check(Windows().Count() == 2 && Windows().All(w => w.BoxId != victim.Id), "删除收纳窗后窗口没有关闭");
            DesktopShelfService.AddBox(S).Name = "新加的"; SettingsService.Instance.Touch();
            Wait(() => Windows().Count() == 3, "运行中新建的收纳窗没有出现");
            S.Enabled = false; SettingsService.Instance.Touch();
            Check(!Windows().Any(), "关闭后仍有收纳窗");
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
            settings.DesktopShelf.Boxes = [new() { Items = [new() { Name = "项目资料", Path = dir, IsFolder = true },
                new() { Name = "安全启动测试", Path = link }] }, new() { Name = "文档", Items = [new() { Name = "Project notes 中英文长标题", Path = file }] }];
            SettingsService.Instance.Apply(settings);
            host.Start(startHidden: true, diagnostic: true);
            host.ShowSettings("desktopshelf");
            Wait(() => Application.Current.Windows.OfType<DesktopShelfWindow>().Count(w => w.IsVisible) == 2, "预览未显示");
            foreach (var window in Application.Current.Windows.OfType<DesktopShelfWindow>()) window.Closed += (_, _) => frame.Continue = false;
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

    internal static void EdgePreview(string dir)
    {
        var original = SettingsService.Instance.Current;
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var hover = typeof(DesktopShelfWindow).GetMethod("HoverTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            Directory.CreateDirectory(dir);
            SettingsService.Instance.Apply(AppSettings.CreateDefault());
            S.Enabled = true; S.AutoHide = true; S.SnapToEdge = true; Box.Width = 280; Box.Height = 300;
            foreach (var theme in Enum.GetValues<ShelfTheme>())
            {
                S.Theme = theme;
                var window = new DesktopShelfWindow(Box, () => { });
                try
                {
                    window.Show(); Await(window.InventoryReady); Pump();
                    var tabs = new List<(ShelfEdge Edge, BitmapSource Tab, Size Size)>();
                    foreach (var hot in new[] { false, true })
                    foreach (var edge in Edges)
                    {
                        window.ExpandFromEdge(); window.FitToScreen(edge); window.PersistGeometry();
                        window.CollapseToEdge();
                        Settle();
                        if (hot) { hover.Invoke(window, [true]); window.UpdateLayout(); }
                        tabs.Add((edge, Render(window, 3), new Size(window.ActualWidth, window.ActualHeight)));
                        if (hot) hover.Invoke(window, [false]);
                    }
                    SaveBoard(tabs, Path.Combine(dir, $"edge-{theme}.png"));
                }
                finally { window.Close(); }
            }
            S.Theme = ShelfTheme.Dark;
            DesktopShelfService.AddBox(S); DesktopShelfService.AddBox(S);
            var windows = S.Boxes.Select(b => new DesktopShelfWindow(b, () => { })).ToList();
            try
            {
                foreach (var w in windows) w.Show();
                Pump();
                var work = ScreenHelper.WorkAreaOf(windows[0]);
                for (var i = 0; i < windows.Count; i++)
                {
                    windows[i].Left = work.Right - windows[i].Width; windows[i].Top = work.Top + 120 + i * 30;
                    windows[i].FitToScreen(ShelfEdge.Right); windows[i].PersistGeometry();
                }
                foreach (var w in windows) w.CollapseToEdge();
                Settle();
                SaveStack(windows, work, Path.Combine(dir, "edge-stacked.png"));
            }
            finally { foreach (var w in windows) w.Close(); }
        }
        finally
        {
            foreach (var window in Application.Current.Windows.OfType<DesktopShelfWindow>().ToArray()) window.Close();
            SettingsService.Instance.Apply(original);
            ThemeService.Apply(original);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    static readonly Brush[] Backdrops =
    [
        new LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x16, 0x2C, 0x46), System.Windows.Media.Color.FromRgb(0x06, 0x10, 0x1C), 90),
        new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF4, 0xF6)),
        new LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x7B, 0xD5), System.Windows.Media.Color.FromRgb(0x8E, 0x4D, 0xC8), 45),
    ];

    /// <summary>窄条放到深色桌面、浅色程序窗口和彩色壁纸三种底上看对比，按 3 倍渲染；一行一种停靠边。</summary>
    static void SaveBoard(IReadOnlyList<(ShelfEdge Edge, BitmapSource Tab, Size Size)> tabs, string path)
    {
        const double cellWidth = 132, cellHeight = 112, scale = 3;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            for (var row = 0; row < tabs.Count; row++)
            for (var column = 0; column < Backdrops.Length; column++)
            {
                var cell = new Rect(column * cellWidth, row * cellHeight, cellWidth, cellHeight);
                dc.DrawRectangle(Backdrops[column], null, cell);
                var (edge, tab, size) = tabs[row];
                var at = edge switch
                {
                    ShelfEdge.Left => new Point(cell.Left, cell.Top + (cell.Height - size.Height) / 2),
                    ShelfEdge.Right => new Point(cell.Right - size.Width, cell.Top + (cell.Height - size.Height) / 2),
                    ShelfEdge.Top => new Point(cell.Left + (cell.Width - size.Width) / 2, cell.Top),
                    _ => new Point(cell.Left + (cell.Width - size.Width) / 2, cell.Bottom - size.Height),
                };
                dc.DrawImage(tab, new Rect(at, size));
            }
        }
        SavePng(visual, cellWidth * Backdrops.Length, cellHeight * tabs.Count, scale, path);
    }

    /// <summary>同一条右边上的几个窄条，按真实相对位置排在三种背景上。</summary>
    static void SaveStack(IReadOnlyList<DesktopShelfWindow> windows, Rect work, string path)
    {
        const double cellWidth = 96, scale = 3;
        var top = windows.Min(w => w.Top) - 16;
        var height = windows.Max(w => w.Top + w.ActualHeight) - top + 16;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            for (var column = 0; column < Backdrops.Length; column++)
            {
                var cell = new Rect(column * cellWidth, 0, cellWidth, height);
                dc.DrawRectangle(Backdrops[column], null, cell);
                foreach (var window in windows)
                    dc.DrawImage(Render(window, scale), new Rect(cell.Right - (work.Right - window.Left), window.Top - top, window.ActualWidth, window.ActualHeight));
            }
        SavePng(visual, cellWidth * Backdrops.Length, height, scale, path);
    }

    static void SavePng(Visual visual, double width, double height, double scale, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path); png.Save(stream);
    }

    static BitmapSource Render(FrameworkElement element, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    static byte[] Pixels(FrameworkElement element, out int width, out int height)
    {
        var bitmap = (RenderTargetBitmap)Render(element, 1);
        width = bitmap.PixelWidth; height = bitmap.PixelHeight;
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    static byte Alpha(byte[] pixels, int width, (int X, int Y) at) => pixels[(at.Y * width + at.X) * 4 + 3];

    static System.Windows.Media.Color PixelColor(byte[] pixels, int width, (int X, int Y) at)
    {
        var i = (at.Y * width + at.X) * 4;
        return System.Windows.Media.Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]);
    }

    /// <summary>等展开/收起动画走完再截图，否则拍到的是半透明的中间帧。</summary>
    static void Settle(int milliseconds = 400)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(10); }
    }

    static List<MenuItem> OpenMenuItems() =>
        PresentationSource.CurrentSources.OfType<HwndSource>().Select(s => s.RootVisual).OfType<DependencyObject>()
            .SelectMany(Descendants<ContextMenu>).Where(m => m.IsOpen).SelectMany(m => m.Items.OfType<MenuItem>()).ToList();

    static void CloseMenus()
    {
        foreach (var menu in PresentationSource.CurrentSources.OfType<HwndSource>().Select(s => s.RootVisual).OfType<DependencyObject>()
            .SelectMany(Descendants<ContextMenu>).ToArray()) menu.IsOpen = false;
        Pump();
    }

    static MouseButtonEventArgs WithClickCount(this MouseButtonEventArgs e, int count)
    {
        typeof(MouseButtonEventArgs).GetMethod("set_ClickCount", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(e, [count]);
        return e;
    }

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static bool Overlaps(Rect a, Rect b)
    {
        var overlap = Rect.Intersect(a, b);
        return !overlap.IsEmpty && overlap.Width > 0.5 && overlap.Height > 0.5;
    }

    static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }

    static void RestoreTestFiles(string directory)
    {
        foreach (var entry in DesktopShelfService.AllItems(S).Where(i => i.IsStored
            && i.OriginalPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray())
            Await(DesktopShelfStorage.Default.RestoreAsync(S, entry));
    }

    static DesktopShelfSettings OneBox(params DesktopShelfEntry[] items)
    {
        var settings = new DesktopShelfSettings { Boxes = [new() { Items = [.. items] }] };
        DesktopShelfService.Normalize(settings);
        return settings;
    }

    static void StorageRecovery() => InTemp(dir =>
    {
        var store = new DesktopShelfStorage(Path.Combine(dir, "store"));
        var sourceDir = Directory.CreateDirectory(Path.Combine(dir, "sources")).FullName;
        var source = Path.Combine(sourceDir, "sample.txt");
        File.WriteAllText(source, "stored content");
        var settings = OneBox(new DesktopShelfEntry { Name = "自定义名称", Path = source });
        Await(store.RecoverAsync(settings));
        Check(File.Exists(source) && !settings.Boxes[0].Items[0].IsStored, "恢复旧配置时擅自移动了文件");
        var added = Await(store.ImportAsync(settings, settings.Boxes[0], [source]));
        Check(added.Added == 1 && settings.Boxes[0].Items.Count == 1 && settings.Boxes[0].Items[0].Name == "自定义名称", "旧入口未原位转换");
        var stored = settings.Boxes[0].Items[0];
        var saved = JsonSerializer.Serialize(new AppSettings { DesktopShelf = settings }, SettingsJson.Default.AppSettings);
        var loaded = JsonSerializer.Deserialize(saved, SettingsJson.Default.AppSettings)!;
        SettingsService.Normalize(loaded);
        Check(loaded.DesktopShelf.Boxes[0].Items[0].StorageId == stored.StorageId && loaded.DesktopShelf.Boxes[0].Items[0].OriginalPath == source,
            "保存/归一化丢失还原记录");
        // 模拟文件已移动、settings.json 尚未保存便退出；独立记录仍能恢复它。
        var recordPath = Path.Combine(store.Root, stored.StorageId, "entry.json");
        var record = JsonSerializer.Deserialize(File.ReadAllText(recordPath), SettingsJson.Default.DesktopShelfRecord)!;
        record.State = "importing";
        File.WriteAllText(recordPath, JsonSerializer.Serialize(record, SettingsJson.Default.DesktopShelfRecord));
        var recovered = new DesktopShelfSettings();
        Check(Await(store.RecoverAsync(recovered)) is null && recovered.Boxes.Count == 1 && recovered.Boxes[0].Items.Count == 1
            && File.ReadAllText(recovered.Boxes[0].Items[0].Path) == "stored content", "中断后无法恢复已移入的文件");
        File.WriteAllText(source, "new source file");
        ThrowsIo(() => Await(store.RestoreAsync(recovered, recovered.Boxes[0].Items[0])));
        Check(File.ReadAllText(source) == "new source file" && File.ReadAllText(stored.Path) == "stored content" && recovered.Boxes[0].Items.Count == 1,
            "还原覆盖了同名文件或丢失收纳项");
        File.Delete(source); Directory.Delete(sourceDir);
        Await(store.RestoreAsync(recovered, recovered.Boxes[0].Items[0]));
        Check(File.ReadAllText(source) == "stored content" && !File.Exists(stored.Path), "原目录删除后不能重新创建并还原");
        File.Delete(source);
        Check(Await(store.RecoverAsync(loaded.DesktopShelf)) is null && loaded.DesktopShelf.Boxes[0].Items.Count == 0,
            "还原完成但旧配置未保存时，重启又出现失效项");

        var outside = Path.Combine(dir, "outside.txt"); File.WriteAllText(outside, "do not touch");
        var forged = new DesktopShelfEntry { StorageId = "../outside", OriginalPath = outside, Path = outside };
        ThrowsIo(() => Await(store.RestoreAsync(new DesktopShelfSettings { Boxes = [new() { Items = [forged] }] }, forged)));
        Check(File.ReadAllText(outside) == "do not touch", "伪造记录改动了收纳目录外文件");
        var empty = OneBox();
        var invalid = Await(store.ImportAsync(empty, empty.Boxes[0], [dir]));
        Check(invalid.Added == 0 && invalid.Error is not null && Directory.Exists(dir), "允许收纳自身上级目录");
        var a = Directory.CreateDirectory(Path.Combine(dir, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(dir, "b")).FullName;
        var pa = Path.Combine(a, "same.txt"); var pb = Path.Combine(b, "same.txt");
        File.WriteAllText(pa, "first"); File.WriteAllText(pb, "second");
        var names = OneBox();
        Check(Await(store.ImportAsync(names, names.Boxes[0], [pa, pb])).Added == 2 && names.Boxes[0].Items.Select(i => i.Path).Distinct().Count() == 2,
            "不同目录的同名文件发生覆盖");
        foreach (var entry in names.Boxes[0].Items.ToArray()) Await(store.RestoreAsync(names, entry));
        Check(File.ReadAllText(pa) == "first" && File.ReadAllText(pb) == "second", "同名文件没有各自还原");
        var folder = Directory.CreateDirectory(Path.Combine(dir, "parent")).FullName;
        var child = Path.Combine(folder, "child.txt"); File.WriteAllText(child, "child");
        var nested = OneBox();
        Check(Await(store.ImportAsync(nested, nested.Boxes[0], [child, folder])).Added == 1 && nested.Boxes[0].Items[0].IsFolder,
            "同时拖入父目录与子文件被拆成两个收纳项");
        Await(store.RestoreAsync(nested, nested.Boxes[0].Items[0]));
        Check(File.ReadAllText(child) == "child", "父子目录批量移动丢失文件");
    });

    static void StorageAcrossBoxes() => InTemp(dir =>
    {
        var store = new DesktopShelfStorage(Path.Combine(dir, "store"));
        var settings = new DesktopShelfSettings { Boxes = [new() { Name = "甲" }, new() { Name = "乙" }] };
        DesktopShelfService.Normalize(settings);
        var (first, second) = (settings.Boxes[0], settings.Boxes[1]);
        var a = Path.Combine(dir, "甲.txt"); var b = Path.Combine(dir, "乙.txt");
        File.WriteAllText(a, "a"); File.WriteAllText(b, "b");
        Check(Await(store.ImportAsync(settings, first, [a])).Added == 1 && Await(store.ImportAsync(settings, second, [b])).Added == 1, "分别移入失败");
        Check(first.Items.Single().OriginalPath == a && second.Items.Single().OriginalPath == b, "项目进错了收纳窗");
        // 已收纳项目的原位置又出现同名文件：哪个收纳窗都不能再收，避免日后还原互相冲突。
        File.WriteAllText(a, "new a");
        var again = Await(store.ImportAsync(settings, second, [a]));
        Check(again.Added == 0 && again.Error is not null && File.ReadAllText(a) == "new a" && second.Items.Count == 1, "跨收纳窗重复收纳了同一原位置");
        File.Delete(a);
        // settings 里丢了第二个收纳窗的项目：盘点补回第一个收纳窗，重复盘点不重复添加。
        var lost = second.Items[0];
        second.Items.Clear();
        Check(Await(store.RecoverAsync(settings)) is null && first.Items.Count == 2 && first.Items.Any(i => i.StorageId == lost.StorageId),
            "丢失的项目没有被盘点回来");
        Check(Await(store.RecoverAsync(settings)) is null && DesktopShelfService.AllItems(settings).Count() == 2, "重复盘点产生重复项");
        foreach (var entry in DesktopShelfService.AllItems(settings).ToArray()) Await(store.RestoreAsync(settings, entry));
        Check(File.ReadAllText(a) == "a" && File.ReadAllText(b) == "b" && !DesktopShelfService.AllItems(settings).Any(), "跨收纳窗还原失败");
        // 旧入口在第二个收纳窗：从第一个收纳窗触发移入，转换后仍留在第二个，保留自定义名称。
        second.Items.Add(new DesktopShelfEntry { Name = "改过的名字", Path = b });
        Check(Await(store.ImportAsync(settings, first, [b])).Added == 1 && first.Items.Count == 0 && second.Items.Single().IsStored
            && second.Items[0].Name == "改过的名字", "旧入口转换后换了收纳窗或丢了名称");
        // 目标收纳窗在移动途中被删除：项目落到第一个收纳窗，不在列表里消失。
        var gone = new DesktopShelfBox { Id = Guid.NewGuid().ToString("N"), Name = "已删除" };
        Check(Await(store.ImportAsync(settings, gone, [a])).Added == 1 && first.Items.Single().OriginalPath == a, "目标收纳窗删除后项目丢失");
        foreach (var entry in DesktopShelfService.AllItems(settings).ToArray()) Await(store.RestoreAsync(settings, entry));
        Check(File.ReadAllText(a) == "a" && File.ReadAllText(b) == "b", "还原后文件内容错误");
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
