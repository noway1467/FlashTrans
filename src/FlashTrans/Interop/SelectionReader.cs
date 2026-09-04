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

    /// <summary>以标准 CF_DIB 格式写入图片，避免依赖 WPF 的延迟渲染。</summary>
    public static void SetImage(CapturedImage image)
    {
        SetImageAndText(image, null);
    }

    /// <summary>一次性写入图片和可选文字，避免后写的文字覆盖图片格式。</summary>
    public static void SetImageAndText(CapturedImage image, string? text)
    {
        if (image.Width <= 0 || image.Height <= 0)
            throw new ArgumentException("图片尺寸无效。", nameof(image));

        var pixelBytes = checked(image.Stride * image.Height);
        var totalBytes = checked((long)40 + pixelBytes);
        Exception? last = null;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (!Win32.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(15);
                continue;
            }

            IntPtr dib = IntPtr.Zero;
            IntPtr textMem = IntPtr.Zero;
            try
            {
                dib = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (IntPtr)totalBytes);
                if (dib == IntPtr.Zero) throw new InvalidOperationException("分配图片剪贴板内存失败。");
                WriteDib(dib, image, pixelBytes);

                if (!string.IsNullOrEmpty(text))
                {
                    var textBytes = checked((text.Length + 1) * 2);
                    textMem = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (IntPtr)textBytes);
                    if (textMem == IntPtr.Zero)
                        throw new InvalidOperationException("分配文字剪贴板内存失败。");
                    WriteText(textMem, text);
                }

                if (!Win32.EmptyClipboard())
                    throw new InvalidOperationException("清空剪贴板失败。");
                if (Win32.SetClipboardData(Win32.CF_DIB, dib) == IntPtr.Zero)
                    throw new InvalidOperationException("写入图片剪贴板数据失败。");
                dib = IntPtr.Zero;

                if (textMem != IntPtr.Zero)
                {
                    if (Win32.SetClipboardData(Win32.CF_UNICODETEXT, textMem) == IntPtr.Zero)
                        throw new InvalidOperationException("写入文字剪贴板数据失败。");
                    textMem = IntPtr.Zero;
                }
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
            finally
            {
                if (dib != IntPtr.Zero) Win32.GlobalFree(dib);
                if (textMem != IntPtr.Zero) Win32.GlobalFree(textMem);
                Win32.CloseClipboard();
            }

            Thread.Sleep(15);
        }

        throw new InvalidOperationException("剪贴板正被其他程序占用，图片未复制。", last);
    }

    static void WriteDib(IntPtr hMem, CapturedImage image, int pixelBytes)
    {
        var p = Win32.GlobalLock(hMem);
        if (p == IntPtr.Zero) throw new InvalidOperationException("锁定图片剪贴板内存失败。");
        try
        {
            Marshal.WriteInt32(p, 0, 40);
            Marshal.WriteInt32(p, 4, image.Width);
            Marshal.WriteInt32(p, 8, image.Height);
            Marshal.WriteInt16(p, 12, 1);
            Marshal.WriteInt16(p, 14, 32);
            Marshal.WriteInt32(p, 16, 0);
            Marshal.WriteInt32(p, 20, pixelBytes);
            Marshal.WriteInt32(p, 24, 3780);
            Marshal.WriteInt32(p, 28, 3780);
            Marshal.WriteInt32(p, 32, 0);
            Marshal.WriteInt32(p, 36, 0);
            for (var y = 0; y < image.Height; y++)
            {
                var source = (image.Height - 1 - y) * image.Stride;
                Marshal.Copy(image.Pixels, source,
                             IntPtr.Add(p, 40 + y * image.Stride), image.Stride);
            }
        }
        finally { Win32.GlobalUnlock(hMem); }
    }

    static void WriteText(IntPtr hMem, string text)
    {
        var p = Win32.GlobalLock(hMem);
        if (p == IntPtr.Zero) throw new InvalidOperationException("锁定文字剪贴板内存失败。");
        try { Marshal.Copy((text + '\0').ToCharArray(), 0, p, text.Length + 1); }
        finally { Win32.GlobalUnlock(hMem); }
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
