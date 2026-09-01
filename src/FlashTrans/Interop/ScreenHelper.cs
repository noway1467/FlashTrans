using System.Windows;
using System.Windows.Media;

namespace FlashTrans.Interop;

/// <summary>多显示器 / 高 DPI 下的定位辅助（返回 WPF 设备无关单位）。</summary>
public static class ScreenHelper
{
    public static POINT CursorPos() => Win32.GetCursorPos(out var p) ? p : default;

    /// <summary>
    /// 让这个窗口对截屏隐身：屏幕上照常看得见，但 BitBlt 拍不到它。
    /// 长截图和录制的浮条用——浮条得显示进度，又不能被拍进成品里。
    /// 返回是不是真的设上了；Win10 2004 以前设不上，那时候只能靠把浮条摆在选区外。
    /// </summary>
    public static bool ExcludeFromCapture(Window w)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            return hwnd != IntPtr.Zero
                && Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
        }
        catch { return false; }
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
