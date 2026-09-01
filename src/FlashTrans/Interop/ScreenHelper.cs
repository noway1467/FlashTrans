using System.Windows;
using System.Windows.Media;

namespace FlashTrans.Interop;

/// <summary>多显示器 / 高 DPI 下的定位辅助（返回 WPF 设备无关单位）。</summary>
public static class ScreenHelper
{
    public static POINT CursorPos() => Win32.GetCursorPos(out var p) ? p : default;

    /// <summary>
    /// 这个点所在显示器的整块范围（含任务栏那条，已换算成 WPF 单位）。
    ///
    /// 跟 <see cref="WorkAreaAt"/> 的差别就是任务栏：最大化的窗口正好占满工作区，
    /// 长截图浮条想躲开选区就只剩任务栏上方那条缝。宁可压在任务栏上，
    /// 也不能压在选区里被拍进长图。
    /// </summary>
    public static Rect MonitorAt(POINT screenPt, Window? scaleRef = null)
    {
        var mon = Win32.MonitorFromPoint(screenPt, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !Win32.GetMonitorInfo(mon, ref mi))
            return WorkAreaAt(screenPt, scaleRef);

        var (sx, sy) = Scale(scaleRef);
        var r = mi.rcMonitor;
        return new Rect(r.Left / sx, r.Top / sy, (r.Right - r.Left) / sx, (r.Bottom - r.Top) / sy);
    }

    /// <summary>
    /// 给浮条找个不压在选区上的位置（进出都是 WPF 单位）。
    ///
    /// 先试选区正下方、正上方，再退到屏幕最底下——也就是压在任务栏上。任务栏那条缝
    /// 常常是唯一的活路：网页长截图基本都是照着最大化的窗口选的，选区正好把工作区占满，
    /// 只按工作区摆就只能摆进选区里。最后才考虑左右两边。
    ///
    /// 一个都塞不下（选区占满整块屏）就挑压得最少的那个，那时候躲不掉了。
    /// </summary>
    public static (double Left, double Top) PlaceOutside(Rect sel, Rect monitor,
                                                         double w, double h, double gap = 8)
    {
        var midX = Math.Clamp((sel.Left + sel.Right) / 2 - w / 2,
                              monitor.Left, Math.Max(monitor.Left, monitor.Right - w));
        var midY = Math.Clamp((sel.Top + sel.Bottom) / 2 - h / 2,
                              monitor.Top, Math.Max(monitor.Top, monitor.Bottom - h));

        var spots = new[]
        {
            new Point(midX, sel.Bottom + gap),      // 选区下面：最顺眼
            new Point(midX, sel.Top - h - gap),     // 选区上面
            new Point(midX, monitor.Bottom - h),    // 贴屏幕最底，压住任务栏
            new Point(midX, monitor.Top),           // 贴屏幕最顶
            new Point(sel.Right + gap, midY),       // 选区右边
            new Point(sel.Left - w - gap, midY),    // 选区左边
            new Point(monitor.Right - w, midY),     // 竖着摆的任务栏那条
            new Point(monitor.Left, midY),
        };

        var best = new Point(midX, Math.Max(monitor.Top, monitor.Bottom - h));
        var least = double.MaxValue;

        foreach (var p in spots)
        {
            var box = new Rect(p.X, p.Y, w, h);
            // 摆到屏幕外面等于没摆，看不见。留半个点的余量：贴边那几个候选算出来
            // 常常差个 1e-13，用 Rect.Contains 会把「正好贴着底边」判成出界。
            if (box.Left < monitor.Left - 0.5 || box.Top < monitor.Top - 0.5
                || box.Right > monitor.Right + 0.5 || box.Bottom > monitor.Bottom + 0.5) continue;

            var over = Rect.Intersect(box, sel);
            var area = over.IsEmpty ? 0 : over.Width * over.Height;
            if (area <= 0) return (p.X, p.Y);
            if (area < least) { least = area; best = p; }
        }
        return (best.X, best.Y);
    }

    /// <summary>光标所在显示器的工作区（已换算成 WPF 单位）。</summary>
    public static Rect WorkAreaAt(POINT screenPt, Window? scaleRef = null)
    {
        var mon = Win32.MonitorFromPoint(screenPt, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !Win32.GetMonitorInfo(mon, ref mi))
            return new Rect(0, 0, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);

        var (sx, sy) = Scale(scaleRef);
        var r = mi.rcWork;
        return new Rect(r.Left / sx, r.Top / sy, (r.Right - r.Left) / sx, (r.Bottom - r.Top) / sy);
    }

    /// <summary>
    /// 窗口所在显示器的工作区。窗口还没有 HWND（没 Show 过）就退回光标那块屏，
    /// 这跟弹窗第一次定位时的取法一致。
    /// </summary>
    public static Rect WorkAreaOf(Window w)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var mon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
                if (mon != IntPtr.Zero && Win32.GetMonitorInfo(mon, ref mi))
                {
                    var (sx, sy) = Scale(w);
                    var r = mi.rcWork;
                    return new Rect(r.Left / sx, r.Top / sy, (r.Right - r.Left) / sx, (r.Bottom - r.Top) / sy);
                }
            }
        }
        catch { /* 取不到就退回光标那块屏 */ }
        return WorkAreaAt(CursorPos(), w);
    }

    public static Point ToDip(POINT screenPt, Window? scaleRef = null)
    {
        var (sx, sy) = Scale(scaleRef);
        return new Point(screenPt.X / sx, screenPt.Y / sy);
    }

    static (double X, double Y) Scale(Window? w)
    {
        try
        {
            var src = w is null ? null : System.Windows.PresentationSource.FromVisual(w);
            var m = src?.CompositionTarget?.TransformToDevice;
            if (m is { } t && t.M11 > 0 && t.M22 > 0) return (t.M11, t.M22);
        }
        catch { /* 窗口还没显示时取不到，退回主屏 DPI */ }

        var dpi = VisualTreeHelper.GetDpi(new System.Windows.Controls.Border());
        return (dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX, dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY);
    }

    /// <summary>把窗口摆到锚点附近，越界时自动翻转/收边。</summary>
    public static (double Left, double Top) PlaceNear(Point anchor, double w, double h, Rect work,
                                                      double gap = 14)
    {
        var left = anchor.X + gap;
        var top = anchor.Y + gap;

        if (left + w > work.Right) left = Math.Max(work.Left, anchor.X - w - gap);
        if (top + h > work.Bottom) top = Math.Max(work.Top, anchor.Y - h - gap);

        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - w));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - h));
        return (left, top);
    }

    /// <summary>确保窗口在可见区域内（恢复保存的位置时用）。</summary>
    public static bool IsOnScreen(double left, double top, double w, double h)
    {
        if (double.IsNaN(left) || double.IsNaN(top)) return false;
        var pt = new POINT { X = (int)(left + w / 2), Y = (int)(top + 20) };
        var work = WorkAreaAt(pt);
        return work.Width > 0 && left + w > work.Left && left < work.Right
                              && top + 30 > work.Top && top < work.Bottom;
    }
}
