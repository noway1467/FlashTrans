using System.Globalization;
using System.IO;
using System.Windows;
using FlashTrans.Core;

namespace FlashTrans.Services;

/// <summary>收纳排列、多个收纳窗的归属与摆位；实际文件归档由 DesktopShelfStorage 处理。</summary>
public static class DesktopShelfService
{
    const double EdgeTolerance = 8;
    public const string DefaultName = "桌面收纳";
    public static readonly (string Label, ShelfSort Value)[] SortOptions =
    [
        ("手动排列", ShelfSort.Manual), ("名称 A → Z", ShelfSort.NameAscending),
        ("名称 Z → A", ShelfSort.NameDescending), ("文件类型", ShelfSort.Type),
        ("最近添加", ShelfSort.AddedNewest), ("使用最多", ShelfSort.MostUsed),
        ("最近打开", ShelfSort.RecentlyUsed),
    ];

    public static void Normalize(DesktopShelfSettings s)
    {
        s.Boxes ??= [];
        List<DesktopShelfEntry>? leftover = null;
        if (s.LegacyItems is not null || s.LegacySort is not null || s.LegacyWidth is not null || s.LegacyHeight is not null
            || s.LegacyLeft is not null || s.LegacyTop is not null || s.LegacyEdge is not null)
        {
            if (s.Boxes.All(b => b is null))
                s.Boxes.Insert(0, new DesktopShelfBox
                {
                    Name = DefaultName, Sort = s.LegacySort ?? ShelfSort.Manual,
                    Width = s.LegacyWidth ?? 280, Height = s.LegacyHeight ?? 340,
                    Left = s.LegacyLeft ?? double.NaN, Top = s.LegacyTop ?? double.NaN,
                    Edge = s.LegacyEdge ?? ShelfEdge.Right, Items = s.LegacyItems ?? [],
                });
            // 新旧字段并存只会出现在降级后又升级：以新结构为准，旧列表里多出的项目补进第一个收纳窗。
            else leftover = s.LegacyItems;
            s.LegacyItems = null; s.LegacySort = null; s.LegacyEdge = null;
            s.LegacyWidth = s.LegacyHeight = s.LegacyLeft = s.LegacyTop = null;
        }

        var boxes = new List<DesktopShelfBox>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        // 同一个收纳项只能属于一个收纳窗，按路径跨窗口去重。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var box in s.Boxes)
        {
            if (box is null) continue;
            box.Items = NormalizeItems(box.Items, seen, QuickLaunchService.MaxItems);
            if (boxes.Count == DesktopShelfSettings.MaxBoxes)
            {
                // 超出上限只可能来自手改配置；并入最后一个收纳窗，不丢项目。
                var last = boxes[^1];
                last.Items.AddRange(box.Items.Take(Math.Max(0, QuickLaunchService.MaxItems - last.Items.Count)));
                continue;
            }
            if (box.Id is null || !Guid.TryParseExact(box.Id, "N", out _) || !ids.Add(box.Id)) ids.Add(box.Id = NewId());
            box.Name = CleanName(box.Name);
            if (!Enum.IsDefined(box.Sort)) box.Sort = ShelfSort.Manual;
            if (!Enum.IsDefined(box.Edge)) box.Edge = ShelfEdge.Right;
            box.Width = Finite(box.Width, 280, DesktopShelfSettings.MinWidth, DesktopShelfSettings.MaxWidth);
            box.Height = Finite(box.Height, 340, DesktopShelfSettings.MinHeight, DesktopShelfSettings.MaxHeight);
            if (!double.IsFinite(box.Left) || !double.IsFinite(box.Top)) box.Left = box.Top = double.NaN;
            boxes.Add(box);
        }
        if (boxes.Count == 0) boxes.Add(new DesktopShelfBox { Id = NewId() });
        if (leftover is not null)
            boxes[0].Items.AddRange(NormalizeItems(leftover, seen, QuickLaunchService.MaxItems - boxes[0].Items.Count));
        foreach (var box in boxes.Where(b => b.Name.Length == 0)) box.Name = UniqueName(boxes);
        s.Boxes = boxes;
        if (!Enum.IsDefined(s.Theme)) s.Theme = ShelfTheme.Dark;
        s.CornerRadius = Finite(s.CornerRadius, 12, 0, 24);
        s.BackgroundOpacity = Finite(s.BackgroundOpacity, 0.92, 0.35, 1);
        s.IconSize = Math.Clamp(s.IconSize, 24, 48);
    }

    static List<DesktopShelfEntry> NormalizeItems(List<DesktopShelfEntry>? items, HashSet<string> seen, int limit)
    {
        var normalized = new List<DesktopShelfEntry>();
        foreach (var original in items ?? [])
        {
            if (normalized.Count >= limit) break;
            if (original is null) continue;
            var item = QuickLaunchService.Normalize([original]).FirstOrDefault();
            if (item is null || !seen.Add(item.Path)) continue;
            normalized.Add(new DesktopShelfEntry { Name = item.Name, Path = item.Path, IsFolder = item.IsFolder,
                LaunchCount = item.LaunchCount, LastLaunched = item.LastLaunched, AddedAt = item.AddedAt,
                StorageId = original.StorageId ?? "", OriginalPath = original.OriginalPath ?? "" });
        }
        return normalized;
    }

    static double Finite(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>名称只保留一行可见文字，按字形截断，避免拆开表情或代理对。</summary>
    public static string CleanName(string? name)
    {
        var text = string.Join(" ", (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        var info = new StringInfo(text);
        return info.LengthInTextElements <= DesktopShelfBox.MaxNameLength ? text : info.SubstringByTextElements(0, DesktopShelfBox.MaxNameLength);
    }

    /// <summary>默认名依次为「桌面收纳」「收纳 2」「收纳 3」……，不与现有名称重复。</summary>
    public static string UniqueName(IEnumerable<DesktopShelfBox> boxes)
    {
        var names = boxes.Select(b => b.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!names.Contains(DefaultName)) return DefaultName;
        for (var i = 2; ; i++)
            if (!names.Contains($"收纳 {i}")) return $"收纳 {i}";
    }

    public static IEnumerable<DesktopShelfEntry> AllItems(DesktopShelfSettings s) => s.Boxes.SelectMany(b => b.Items);

    public static DesktopShelfBox? BoxOf(DesktopShelfSettings s, DesktopShelfEntry entry) =>
        s.Boxes.FirstOrDefault(b => b.Items.Contains(entry));

    /// <summary>新收纳窗用紧凑尺寸，停靠在第一个收纳窗所在的边；坐标等窗口显示时按屏幕空位决定。</summary>
    public static DesktopShelfBox AddBox(DesktopShelfSettings s)
    {
        if (s.Boxes.Count >= DesktopShelfSettings.MaxBoxes)
            throw new InvalidOperationException($"最多创建 {DesktopShelfSettings.MaxBoxes} 个收纳窗");
        var edge = s.Boxes.FirstOrDefault()?.Edge ?? ShelfEdge.Right;
        var box = new DesktopShelfBox { Id = NewId(), Name = UniqueName(s.Boxes), Width = 240, Height = 260,
            Edge = edge == ShelfEdge.None ? ShelfEdge.Right : edge };
        s.Boxes.Add(box);
        return box;
    }

    /// <summary>删除收纳窗只改列表归属：项目并入剩下的第一个收纳窗，不移动任何文件。返回接收项目的收纳窗。</summary>
    public static DesktopShelfBox RemoveBox(DesktopShelfSettings s, DesktopShelfBox box)
    {
        if (!s.Boxes.Contains(box)) throw new InvalidOperationException("该收纳窗已经删除");
        if (s.Boxes.Count <= 1) throw new InvalidOperationException("至少保留一个收纳窗；不用时可在设置中关闭桌面收纳");
        var heir = s.Boxes.First(b => !ReferenceEquals(b, box));
        if (heir.Items.Count + box.Items.Count > QuickLaunchService.MaxItems)
            throw new InvalidOperationException($"合并后超过 {QuickLaunchService.MaxItems} 项，请先解除部分项目");
        heir.Items.AddRange(box.Items);
        s.Boxes.Remove(box);
        return heir;
    }

    /// <summary>把项目移到另一个收纳窗，同样只改归属，收纳目录里的文件保持不动。</summary>
    public static bool MoveEntry(DesktopShelfSettings s, DesktopShelfEntry entry, DesktopShelfBox target)
    {
        var from = BoxOf(s, entry);
        if (from is null || ReferenceEquals(from, target) || !s.Boxes.Contains(target)) return false;
        if (target.Items.Count >= QuickLaunchService.MaxItems)
            throw new InvalidOperationException($"「{target.Name}」已有 {QuickLaunchService.MaxItems} 项，请先解除不再使用的项目");
        from.Items.Remove(entry);
        target.Items.Add(entry);
        return true;
    }

    public static List<DesktopShelfEntry> Ordered(DesktopShelfBox box) => (box.Sort switch
    {
        ShelfSort.NameAscending => box.Items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        ShelfSort.NameDescending => box.Items.OrderByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        ShelfSort.Type => box.Items.OrderBy(i => i.IsFolder ? 0 : 1)
            .ThenBy(i => Path.GetExtension(i.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        ShelfSort.AddedNewest => box.Items.OrderByDescending(i => i.AddedAt),
        ShelfSort.MostUsed => box.Items.OrderByDescending(i => i.LaunchCount),
        ShelfSort.RecentlyUsed => box.Items.OrderByDescending(i => i.LastLaunched),
        _ => (IEnumerable<DesktopShelfEntry>)box.Items,
    }).ToList();

    /// <summary>交换当前可见位置，同时切回手动排序，避免排序规则立刻把图标排回去。</summary>
    public static bool Swap(DesktopShelfBox box, string source, string target)
    {
        var items = Ordered(box);
        var from = items.FindIndex(i => string.Equals(i.Path, source, StringComparison.OrdinalIgnoreCase));
        var to = items.FindIndex(i => string.Equals(i.Path, target, StringComparison.OrdinalIgnoreCase));
        if (from < 0 || to < 0 || from == to) return false;
        (items[from], items[to]) = (items[to], items[from]);
        box.Items = items;
        box.Sort = ShelfSort.Manual;
        return true;
    }

    public static (Point Position, ShelfEdge Edge) Place(Rect box, Rect work, bool snap, ShelfEdge edge = ShelfEdge.None)
    {
        var x = Math.Clamp(box.Left, work.Left, Math.Max(work.Left, work.Right - box.Width));
        var y = Math.Clamp(box.Top, work.Top, Math.Max(work.Top, work.Bottom - box.Height));
        var nearest = new[] { (ShelfEdge.Left, Math.Abs(x - work.Left)), (ShelfEdge.Right, Math.Abs(work.Right - x - box.Width)),
            (ShelfEdge.Top, Math.Abs(y - work.Top)), (ShelfEdge.Bottom, Math.Abs(work.Bottom - y - box.Height)) }
            .MinBy(pair => pair.Item2);
        // 关闭自动吸附只禁止移动窗口；手动贴近边缘时仍要识别停靠边，供自动隐藏使用。
        if (!snap) return (new Point(x, y), nearest.Item2 <= EdgeTolerance ? nearest.Item1 : ShelfEdge.None);
        if (edge == ShelfEdge.None) edge = nearest.Item1;
        if (edge == ShelfEdge.Left) x = work.Left;
        if (edge == ShelfEdge.Right) x = Math.Max(work.Left, work.Right - box.Width);
        if (edge == ShelfEdge.Top) y = work.Top;
        if (edge == ShelfEdge.Bottom) y = Math.Max(work.Top, work.Bottom - box.Height);
        return (new Point(x, y), edge);
    }

    /// <summary>
    /// 给还没有位置的收纳窗找空位：沿停靠边自上而下，避开已有收纳窗，放不下再换到对侧。
    /// 整屏都放不下时错开叠放，至少露出标题栏，方便拖走。
    /// </summary>
    public static (Point Position, ShelfEdge Edge) FreeSlot(Size size, Rect work, IReadOnlyCollection<Rect> taken, ShelfEdge prefer)
    {
        const double gap = 12;
        foreach (var edge in prefer == ShelfEdge.Left ? new[] { ShelfEdge.Left, ShelfEdge.Right } : new[] { ShelfEdge.Right, ShelfEdge.Left })
        {
            var x = edge == ShelfEdge.Left ? work.Left : work.Right - size.Width;
            // 优先与第一个收纳窗的默认位置对齐，其次紧挨已有收纳窗下方，最后才贴到最顶端。
            foreach (var y in taken.Select(r => r.Bottom + gap).Order().Prepend(work.Top + 64).Append(work.Top))
            {
                var slot = new Rect(x, y, size.Width, size.Height);
                if (slot.Top >= work.Top && slot.Bottom <= work.Bottom
                    && !taken.Any(r => r.Left < slot.Right + gap && slot.Left < r.Right + gap && r.Top < slot.Bottom + gap && slot.Top < r.Bottom + gap))
                    return (slot.Location, edge);
            }
        }
        var top = taken.Count == 0 ? work.Top + 64 : taken.Max(r => r.Top) + 36;
        return (new Point(work.Right - size.Width, Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - size.Height))), ShelfEdge.Right);
    }

    /// <summary>同一条边上的窄条依次错开，避免多个收纳窗收起后叠在一起点不到。返回沿边方向的起点。</summary>
    public static double TabOffset(double desired, double length, double min, double max, IEnumerable<(double Start, double End)> others, double gap)
    {
        var taken = others.ToList();
        var high = Math.Max(min, max - length);
        bool Free(double start) => start >= min - 0.01 && start <= high + 0.01
            && taken.All(o => start + length + gap <= o.Start + 0.01 || start >= o.End + gap - 0.01);
        desired = Math.Clamp(desired, min, high);
        if (Free(desired)) return desired;
        return taken.SelectMany(o => new[] { o.Start - gap - length, o.End + gap }).Where(Free)
            .DefaultIfEmpty(desired).MinBy(start => Math.Abs(start - desired));
    }
}
