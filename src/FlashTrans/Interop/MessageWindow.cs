using System.Windows.Interop;

namespace FlashTrans.Interop;

/// <summary>隐藏的消息窗口，承载热键、托盘与剪贴板监听。启动只需几毫秒。</summary>
public sealed class MessageWindow : IDisposable
{
    readonly HwndSource _source;

    public IntPtr Handle => _source.Handle;

    /// <summary>返回 true 表示消息已处理。</summary>
    public event Func<int, IntPtr, IntPtr, bool>? Message;

    public MessageWindow(string name)
    {
        _source = new HwndSource(new HwndSourceParameters(name)
        {
            Width = 0, Height = 0, PositionX = -10000, PositionY = -10000,
            WindowStyle = 0,
            ExtendedWindowStyle = Win32.WS_EX_TOOLWINDOW,
        });
        _source.AddHook(Hook);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (Message?.Invoke(msg, wParam, lParam) == true) handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(Hook);
        _source.Dispose();
    }
}
