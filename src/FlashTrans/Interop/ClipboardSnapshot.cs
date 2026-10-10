using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FlashTrans.Interop;

/// <summary>用户数据格式的独立副本；无法完整复制时拒绝借用，不降级为纯文本。</summary>
internal sealed class ClipboardSnapshot : IDisposable
{
    readonly List<(uint Format, IntPtr Data)> _items = [];
    static readonly uint DataObjectFormat = RegisterClipboardFormat("DataObject");
    static readonly uint OlePrivateFormat = RegisterClipboardFormat("Ole Private Data");
    public uint Sequence { get; private set; }
    internal static ClipboardSnapshot Capture()
    {
        var snapshot = new ClipboardSnapshot();
        if (!Open()) throw new InvalidOperationException("剪贴板正被占用，未执行取词。");
        try
        {
            long total = 0;
            uint format = 0;
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                format = EnumClipboardFormats(format);
                if (format == 0)
                {
                    if (Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
                    break;
                }
                // 这两种是 OLE 的对象引用/格式传输表，不是用户数据。原始字节中的引用
                // 会随 EmptyClipboard 失效，照搬会让其他进程 OleGetClipboard 报 BAD_DATA。
                // 实际内容先全部物化保存；还原后由 OLE 为这些原生格式创建新的包装对象。
                if (format == DataObjectFormat || format == OlePrivateFormat) continue;
                // 私有格式的句柄释放规则由原应用定义，不能猜测其内存布局。
                if (format == 0x80 || format is >= 0x200 and <= 0x3FF || snapshot._items.Count >= 256)
                    throw new InvalidOperationException("剪贴板含无法安全备份的格式，已跳过取词；请手动复制或截图。");
                var data = Win32.GetClipboardData(format);
                if (data == IntPtr.Zero) throw new InvalidOperationException("剪贴板尚未就绪，已跳过取词以保护原内容。");
                // GDI 句柄不是 HGLOBAL，不能拿去调用 GlobalSize/GlobalLock。
                var gdi = format is 2 or 9 or 14 or 0x82 or 0x8E;
                var size = gdi ? 0 : Win32.GlobalSize(data).ToInt64();
                if (!gdi && size <= 0)
                    throw new InvalidOperationException("剪贴板格式无法完整备份，已跳过取词。");
                if (format is 2 or 0x82)
                {
                    if (GetObject(data, Marshal.SizeOf<BitmapInfo>(), out var bitmap) == 0)
                        throw new InvalidOperationException("无法确认剪贴板图片大小，已跳过取词。");
                    size = Math.Abs((long)bitmap.Height) * Math.Abs((long)bitmap.WidthBytes);
                }
                else if (format is 14 or 0x8E) size = GetEnhMetaFileBits(data, 0, IntPtr.Zero);
                total += Math.Max(0, size);
                if (total > 128 * 1024 * 1024) throw new InvalidOperationException("剪贴板超过 128 MB，已跳过取词。");
                var baseFormat = format switch { 0x82 => 2u, 0x83 => 3u, 0x8E => 14u, _ => format };
                var copy = baseFormat == 14 ? CopyEnhMetaFile(data, null) : OleDuplicateData(data, (ushort)baseFormat, 0);
                if (copy == IntPtr.Zero) throw new InvalidOperationException("剪贴板备份失败，未执行取词。");
                snapshot._items.Add((format, copy));
            }
            snapshot.Sequence = Win32.GetClipboardSequenceNumber();
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
        finally { Win32.CloseClipboard(); }
    }

    /// <summary>在同一把系统锁内检查序列号并还原，避免覆盖用户新复制的内容。</summary>
    internal bool Restore(uint expectedSequence)
    {
        var owner = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (owner == IntPtr.Zero) throw new Win32Exception();
        try
        {
            if (!Open(owner)) throw new InvalidOperationException("剪贴板被占用，无法恢复原内容。");
            try
            {
                if (Win32.GetClipboardSequenceNumber() != expectedSequence) return false;
                if (!Win32.EmptyClipboard()) throw new Win32Exception();
                for (var i = 0; i < _items.Count; i++)
                {
                    var (format, data) = _items[i];
                    if (Win32.SetClipboardData(format, data) == IntPtr.Zero) throw new Win32Exception();
                    _items[i] = (format, IntPtr.Zero);
                }
                return true;
            }
            finally { Win32.CloseClipboard(); }
        }
        finally { DestroyWindow(owner); }
    }

    internal static bool Open(IntPtr owner = default)
    {
        for (var i = 0; i < 12; i++)
        {
            if (Win32.OpenClipboard(owner)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    public void Dispose()
    {
        foreach (var (format, data) in _items)
        {
            if (data == IntPtr.Zero) continue;
            if (format is 2 or 9 or 0x82) Win32.DeleteObject(data);
            else if (format is 14 or 0x8E) DeleteEnhMetaFile(data);
            else
            {
                if (format is 3 or 0x83)
                {
                    var ptr = Win32.GlobalLock(data);
                    if (ptr != IntPtr.Zero)
                    {
                        DeleteMetaFile(Marshal.PtrToStructure<MetaFilePicture>(ptr).Handle);
                        Win32.GlobalUnlock(data);
                    }
                }
                Win32.GlobalFree(data);
            }
        }
        _items.Clear();
    }

    [StructLayout(LayoutKind.Sequential)] struct MetaFilePicture { public int Mode, X, Y; public IntPtr Handle; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern int GetObject(IntPtr data, int size, out BitmapInfo bitmap);
    [DllImport("gdi32.dll")] static extern uint GetEnhMetaFileBits(IntPtr data, uint size, IntPtr bytes);
    [DllImport("user32.dll", SetLastError = true)] static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("ole32.dll", SetLastError = true)] static extern IntPtr OleDuplicateData(IntPtr data, ushort format, uint flags);
    [DllImport("gdi32.dll")] static extern bool DeleteEnhMetaFile(IntPtr data);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CopyEnhMetaFile(IntPtr data, string? file);
    [DllImport("gdi32.dll")] static extern bool DeleteMetaFile(IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr GetClipboardOwner();
}
