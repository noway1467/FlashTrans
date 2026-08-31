using System.IO;
using FlashTrans.Services;

namespace FlashTrans.Interop;

/// <summary>托盘图标（直接用 Shell_NotifyIcon，不依赖 WinForms）。</summary>
public sealed class TrayIcon : IDisposable
{
    readonly IntPtr _hwnd;
    IntPtr _icon;
    bool _added;
    const uint IconId = 1;

    public event Action? LeftClick;
    public event Action? DoubleClick;
    public event Action? RightClick;

    public TrayIcon(IntPtr hwnd, string tip)
    {
        _hwnd = hwnd;
        _icon = LoadIcon();
        var data = Build(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP);
        data.szTip = Truncate(tip, 127);
        _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref data);
        if (!_added) Log.Warn("托盘图标创建失败");
    }

    NOTIFYICONDATA Build(uint flags) => new()
    {
        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = Win32.WM_TRAY,
        hIcon = _icon,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    static IntPtr LoadIcon()
    {
        // 通知区域按系统小图标尺寸取，高 DPI 下是 20/24 而不是 16，取错会被拉伸发虚。
        var cx = Math.Max(16, Win32.GetSystemMetrics(Win32.SM_CXSMICON));
        var cy = Math.Max(16, Win32.GetSystemMetrics(Win32.SM_CYSMICON));

        // 优先读 .ico 文件：LoadImage + LR_LOADFROMFILE 只认 ico/cur/bmp，喂 exe 会返回 NULL。
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(ico))
            {
                var h = Win32.LoadImage(IntPtr.Zero, ico, Win32.IMAGE_ICON, cx, cy, Win32.LR_LOADFROMFILE);
                if (h != IntPtr.Zero) return h;
            }
        }
        catch (Exception ex) { Log.Warn("托盘图标读 ico 失败：" + ex.Message); }

        // 退路：从 exe 自身的资源里抽。单文件发布时 Assets 可能不在磁盘上。
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var small = new IntPtr[1];
                if (Win32.ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
                    return small[0];
            }
        }
        catch (Exception ex) { Log.Warn("托盘图标抽 exe 资源失败：" + ex.Message); }

        Log.Warn("托盘图标没能加载，通知区域会是空白");
        return IntPtr.Zero;
    }

    public void UpdateTip(string tip)
    {
        if (!_added) return;
        var data = Build(Win32.NIF_TIP);
        data.szTip = Truncate(tip, 127);
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
    }

    // 气泡通知（NIM_MODIFY + NIF_INFO）去掉了：系统会按「专注助手」和
    // 通知开关静默丢弃，用户什么都看不到。提示改走 ToastWindow 自己画。
    // NOTIFYICONDATA 里的 szInfo 等字段留着，结构体大小不能变。

    /// <summary>由消息窗口转发 WM_TRAY。</summary>
    public void Handle(IntPtr lParam)
    {
        switch (lParam.ToInt32())
        {
            case Win32.WM_LBUTTONUP: LeftClick?.Invoke(); break;
            case Win32.WM_LBUTTONDBLCLK: DoubleClick?.Invoke(); break;
            case Win32.WM_RBUTTONUP: RightClick?.Invoke(); break;
        }
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        if (_added)
        {
            var data = Build(0);
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero)
        {
            Win32.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }
}
