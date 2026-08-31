using System.Runtime.InteropServices;
using System.Windows.Input;
using FlashTrans.Services;

namespace FlashTrans.Interop;

/// <summary>低级鼠标钩子：识别「拖选」和「双击选词」，只在需要时才启用。</summary>
public sealed class MouseSelectionHook : IDisposable
{
    const int DragThreshold = 6;
    const int DoubleClickMs = 420;

    readonly Win32.HookProc _proc;   // 必须保持引用，否则会被 GC 回收导致钩子失效
    IntPtr _hook;
    POINT _downAt;
    int _downTick;
    int _lastUpTick;
    POINT _lastUpAt;

    /// <summary>参数为鼠标释放位置（屏幕坐标）。</summary>
    public event Action<POINT>? SelectionMade;

    /// <summary>返回 true 表示当前应当忽略（例如焦点在本程序窗口上）。</summary>
    public Func<bool>? ShouldIgnore { get; set; }

    /// <summary>需要按住的修饰键，none 表示不要求。</summary>
    public string RequiredModifier { get; set; } = "none";

    public bool IsRunning => _hook != IntPtr.Zero;

    public MouseSelectionHook() => _proc = HookCallback;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _proc, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Warn("鼠标钩子安装失败：" + Marshal.GetLastWin32Error());
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
            var msg = wParam.ToInt32();
            if (msg is Win32.WM_LBUTTONDOWN or Win32.WM_LBUTTONUP or Win32.WM_LBUTTONDBLCLK)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                switch (msg)
                {
                    case Win32.WM_LBUTTONDOWN:
                        _downAt = data.pt;
                        _downTick = Environment.TickCount;
                        break;

                    case Win32.WM_LBUTTONDBLCLK:
                        Fire(data.pt);
                        break;

                    case Win32.WM_LBUTTONUP:
                        var now = Environment.TickCount;
                        var dragged = Math.Abs(data.pt.X - _downAt.X) >= DragThreshold ||
                                      Math.Abs(data.pt.Y - _downAt.Y) >= DragThreshold;
                        // 双击有时不产生 DBLCLK（如某些浏览器），用两次抬起的间隔兜底
                        var doubled = now - _lastUpTick <= DoubleClickMs &&
                                      Math.Abs(data.pt.X - _lastUpAt.X) < 5 &&
                                      Math.Abs(data.pt.Y - _lastUpAt.Y) < 5;
                        _lastUpTick = now;
                        _lastUpAt = data.pt;
                        if (dragged || doubled) Fire(data.pt);
                        break;
                }
            }
        }
        catch (Exception ex) { Log.Warn("鼠标钩子异常：" + ex.Message); }

        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    void Fire(POINT pt)
    {
        if (!ModifierOk()) return;
        if (ShouldIgnore?.Invoke() == true) return;
        SelectionMade?.Invoke(pt);
    }

    bool ModifierOk() => RequiredModifier.ToLowerInvariant() switch
    {
        "ctrl" => Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
        "alt" => Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),
        "shift" => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
        _ => true
    };

    public void Dispose() => Stop();
}
