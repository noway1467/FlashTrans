using System.IO;
using System.Windows;
using FlashTrans.Core;

namespace FlashTrans.Services;

/// <summary>收纳排列与外观；实际文件归档由 DesktopShelfStorage 处理。</summary>
public static class DesktopShelfService
{
    const double EdgeTolerance = 8;
    public static readonly (string Label, ShelfSort Value)[] SortOptions =
    [
        ("手动排列", ShelfSort.Manual), ("名称 A → Z", ShelfSort.NameAscending),
        ("名称 Z → A", ShelfSort.NameDescending), ("文件类型", ShelfSort.Type),
        ("最近添加", ShelfSort.AddedNewest), ("使用最多", ShelfSort.MostUsed),
        ("最近打开", ShelfSort.RecentlyUsed),
    ];

    public static void Normalize(DesktopShelfSettings s)
    {
        var normalized = new List<DesktopShelfEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var original in s.Items ?? [])
        {
            if (original is null) continue;
            var item = QuickLaunchService.Normalize([original]).FirstOrDefault();
            if (item is null || !seen.Add(item.Path)) continue;
            normalized.Add(new DesktopShelfEntry { Name = item.Name, Path = item.Path, IsFolder = item.IsFolder,
                LaunchCount = item.LaunchCount, LastLaunched = item.LastLaunched, AddedAt = item.AddedAt,
                StorageId = original.StorageId ?? "", OriginalPath = original.OriginalPath ?? "" });
            if (normalized.Count == QuickLaunchService.MaxItems) break;
        }
        s.Items = normalized;
        if (!Enum.IsDefined(s.Theme)) s.Theme = ShelfTheme.Dark;
        if (!Enum.IsDefined(s.Sort)) s.Sort = ShelfSort.Manual;
        if (!Enum.IsDefined(s.Edge)) s.Edge = ShelfEdge.Right;
        s.CornerRadius = Finite(s.CornerRadius, 12, 0, 24);
        s.BackgroundOpacity = Finite(s.BackgroundOpacity, 0.92, 0.35, 1);
        s.IconSize = Math.Clamp(s.IconSize, 24, 48);
        s.Width = Finite(s.Width, 280, DesktopShelfSettings.MinWidth, DesktopShelfSettings.MaxWidth);
        s.Height = Finite(s.Height, 340, DesktopShelfSettings.MinHeight, DesktopShelfSettings.MaxHeight);
        if (!double.IsFinite(s.Left) || !double.IsFinite(s.Top)) s.Left = s.Top = double.NaN;
    }

    static double Finite(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    public static List<DesktopShelfEntry> Ordered(DesktopShelfSettings s) => (s.Sort switch
    {
        ShelfSort.NameAscending => s.Items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        ShelfSort.NameDescending => s.Items.OrderByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        ShelfSort.Type => s.Items.OrderBy(i => i.IsFolder ? 0 : 1)
            .ThenBy(i => Path.GetExtension(i.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        ShelfSort.AddedNewest => s.Items.OrderByDescending(i => i.AddedAt),
        ShelfSort.MostUsed => s.Items.OrderByDescending(i => i.LaunchCount),
        ShelfSort.RecentlyUsed => s.Items.OrderByDescending(i => i.LastLaunched),
        _ => (IEnumerable<DesktopShelfEntry>)s.Items,
    }).ToList();

    /// <summary>交换当前可见位置，同时切回手动排序，避免排序规则立刻把图标排回去。</summary>
    public static bool Swap(DesktopShelfSettings s, string source, string target)
    {
        var items = Ordered(s);
        var from = items.FindIndex(i => string.Equals(i.Path, source, StringComparison.OrdinalIgnoreCase));
        var to = items.FindIndex(i => string.Equals(i.Path, target, StringComparison.OrdinalIgnoreCase));
        if (from < 0 || to < 0 || from == to) return false;
        (items[from], items[to]) = (items[to], items[from]);
        s.Items = items;
        s.Sort = ShelfSort.Manual;
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
}
