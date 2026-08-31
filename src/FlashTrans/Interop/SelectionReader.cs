using System.Runtime.InteropServices;
using FlashTrans.Services;

namespace FlashTrans.Interop;

/// <summary>读取当前选中的文本：模拟 Ctrl+C 后取剪贴板，随后恢复原内容。</summary>
public static class SelectionReader
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<string?> GetSelectedTextAsync(bool restoreClipboard, CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(1500, ct)) return null;
        try
        {
            var backup = restoreClipboard ? ReadText() : null;
            var before = Win32.GetClipboardSequenceNumber();

            ReleaseModifiers();
            await Task.Delay(20, ct).ConfigureAwait(false);
            SendCtrlC();

            string? text = null;
            // 只在序列号变了之后才去开剪贴板。OpenClipboard 是全局锁，
            // 空转着开开关关会把前台程序（尤其浏览器）一起拖住。
            for (int i = 0; i < 24; i++)
            {
                await Task.Delay(i < 8 ? 12 : 25, ct).ConfigureAwait(false);
                if (Win32.GetClipboardSequenceNumber() == before) continue;
                text = ReadText();
                if (!string.IsNullOrEmpty(text)) break;
            }

            if (restoreClipboard && backup is not null && !string.IsNullOrEmpty(text))
            {
                await Task.Delay(30, ct).ConfigureAwait(false);
                SetText(backup);
            }
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Warn("读取选中文本失败：" + ex.Message);
            return null;
        }
        finally { Gate.Release(); }
    }

    /// <summary>读剪贴板文本。走原生 API，一次开关只做一件事，尽快释放全局锁。</summary>
    public static string? ReadText()
    {
        if (!Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT)) return null;

        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (!Win32.OpenClipboard(IntPtr.Zero)) { Thread.Sleep(15); continue; }
            try
            {
                var h = Win32.GetClipboardData(Win32.CF_UNICODETEXT);
                if (h == IntPtr.Zero) return null;

                var p = Win32.GlobalLock(h);
                if (p == IntPtr.Zero) return null;
                try
                {
                    // GlobalSize 是分配量，可能带填充，交给 PtrToStringUni 找结尾的 \0
                    var max = (int)Win32.GlobalSize(h) / 2;
                    return max <= 0 ? null : Marshal.PtrToStringUni(p, max)?.TrimEnd('\0');
                }
                finally { Win32.GlobalUnlock(h); }
            }
            finally { Win32.CloseClipboard(); }
        }
        return null;
    }

    /// <summary>写剪贴板文本。不做 OLE flush，避免同步刷盘造成的卡顿。</summary>
    public static void SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (!Win32.OpenClipboard(IntPtr.Zero)) { Thread.Sleep(15); continue; }

            var bytes = (IntPtr)((text.Length + 1) * 2);
            var hMem = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, bytes);
            if (hMem == IntPtr.Zero) { Win32.CloseClipboard(); return; }

            try
            {
                var p = Win32.GlobalLock(hMem);
                if (p == IntPtr.Zero) { Win32.GlobalFree(hMem); return; }
                try { Marshal.Copy((text + '\0').ToCharArray(), 0, p, text.Length + 1); }
                finally { Win32.GlobalUnlock(hMem); }

                Win32.EmptyClipboard();
                // 交给系统后不能再 GlobalFree，所有权已转移
                if (Win32.SetClipboardData(Win32.CF_UNICODETEXT, hMem) == IntPtr.Zero)
                    Win32.GlobalFree(hMem);
                return;
            }
            finally { Win32.CloseClipboard(); }
        }
    }

    /// <summary>热键可能仍按住 Alt/Shift/Win，先松开，否则 Ctrl+C 会变成组合键。</summary>
    static void ReleaseModifiers()
    {
        var inputs = new List<INPUT>();
        foreach (var vk in (int[])[Win32.VK_MENU, Win32.VK_SHIFT, Win32.VK_LWIN])
            if ((Win32.GetAsyncKeyState(vk) & 0x8000) != 0)
                inputs.Add(KeyInput((ushort)vk, up: true));
        if (inputs.Count > 0)
            Win32.SendInput((uint)inputs.Count, inputs.ToArray(), System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    static void SendCtrlC()
    {
        bool ctrlHeld = (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;
        var seq = new List<INPUT>();
        if (!ctrlHeld) seq.Add(KeyInput(Win32.VK_CONTROL, up: false));
        seq.Add(KeyInput(Win32.VK_C, up: false));
        seq.Add(KeyInput(Win32.VK_C, up: true));
        if (!ctrlHeld) seq.Add(KeyInput(Win32.VK_CONTROL, up: true));
        Win32.SendInput((uint)seq.Count, seq.ToArray(), System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? Win32.KEYEVENTF_KEYUP : 0 }
        }
    };

    static INPUT KeyInput(int vk, bool up) => KeyInput((ushort)vk, up);
}
