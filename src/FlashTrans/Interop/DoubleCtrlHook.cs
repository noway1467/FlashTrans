using System.Runtime.InteropServices;
using FlashTrans.Services;

namespace FlashTrans.Interop;

/// <summary>连按两次 Ctrl 唤出翻译（可选功能，关闭时不安装钩子）。</summary>
public sealed class DoubleCtrlHook : IDisposable
{
    const int WindowMs = 380;

    readonly Win32.HookProc _proc;
    IntPtr _hook;
    int _lastUpTick;
    bool _otherKeyPressed;

    public event Action? Triggered;

    public bool IsRunning => _hook != IntPtr.Zero;

    public DoubleCtrlHook() => _proc = Callback;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Warn("键盘钩子安装失败：" + Marshal.GetLastWin32Error());
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
            var msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool isCtrl = info.vkCode is 0xA2 or 0xA3 or 0x11;

            if (msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
            {
                if (!isCtrl) _otherKeyPressed = true;
            }
            else if (msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP && isCtrl)
            {
                var now = Environment.TickCount;
                if (!_otherKeyPressed && now - _lastUpTick is > 40 and <= WindowMs)
                {
                    _lastUpTick = 0;
                    Triggered?.Invoke();
                }
                else _lastUpTick = now;
                _otherKeyPressed = false;
            }
        }
        catch (Exception ex) { Log.Warn("键盘钩子异常：" + ex.Message); }

        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
