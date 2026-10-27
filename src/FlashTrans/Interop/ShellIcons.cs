using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FlashTrans.Interop;

/// <summary>
/// 取文件/文件夹的系统图标并判断目标是否还在。都放在一条后台 STA 线程里做：
/// 离线的网络盘、拔掉的移动硬盘上，一次 Exists 或取图标就可能卡好几秒，不能拖慢面板呼出。
/// </summary>
internal static class ShellIcons
{
    readonly record struct Job(string Path, bool Folder, Dispatcher Dispatcher, Action<ImageSource?, bool> Done);

    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    // 存在性只信 30 秒：搜索框每敲一个字都会重建列表，不能每次都去碰一遍离线的网络盘。
    static readonly ConcurrentDictionary<string, (bool Exists, long Tick)> Checked = new(StringComparer.OrdinalIgnoreCase);
    const long CheckedLifetimeMs = 30_000;
    static readonly BlockingCollection<Job> Queue = [];
    static readonly object Gate = new();
    static Thread? _worker;
    static IntPtr _extraLarge;

    /// <summary>
    /// 回调回到调用方线程；图标取不到时给 null，界面保留矢量占位图标。
    /// 近期查过的路径直接同步回调，调用方须在调用前把回调里要改的元素建好。
    /// </summary>
    public static void Request(string path, bool folder, Action<ImageSource?, bool> done)
    {
        if (Checked.TryGetValue(path, out var known) && Environment.TickCount64 - known.Tick < CheckedLifetimeMs)
        {
            if (!known.Exists) { done(null, false); return; }
            if (Cache.TryGetValue(path, out var cached)) { done(cached, true); return; }
        }
        lock (Gate)
        {
            if (_worker is null)
            {
                _worker = new Thread(Work) { IsBackground = true, Name = "FlashTrans.ShellIcons" };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();
            }
        }
        Queue.Add(new Job(path, folder, Dispatcher.CurrentDispatcher, done));
    }

    static void Work()
    {
        foreach (var job in Queue.GetConsumingEnumerable())
        {
            ImageSource? icon = null;
            bool exists;
            try
            {
                exists = job.Folder ? Directory.Exists(job.Path) : File.Exists(job.Path);
                // 不存在的目标不取图标：系统会按扩展名给个通用图标，反倒看不出它已失效。
                if (exists && !Cache.TryGetValue(job.Path, out icon)) Cache[job.Path] = icon = Load(job.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException)
            {
                exists = false;
            }
            Checked[job.Path] = (exists, Environment.TickCount64);
            job.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => job.Done(icon, exists));
        }
    }

    static ImageSource? Load(string path)
    {
        var info = new SHFILEINFO();
        var size = (uint)Marshal.SizeOf<SHFILEINFO>();
        var icon = IntPtr.Zero;
        // 48px 的系统图像列表，高 DPI 下比 SHGFI_LARGEICON 的 32px 清楚；拿不到再退回 32px。
        if (SHGetFileInfo(path, 0, ref info, size, SHGFI_SYSICONINDEX) != IntPtr.Zero)
        {
            if (_extraLarge == IntPtr.Zero)
            {
                var iid = IID_IImageList;
                if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out var list) == 0) _extraLarge = list;
            }
            if (_extraLarge != IntPtr.Zero) icon = ImageList_GetIcon(_extraLarge, info.iIcon, ILD_TRANSPARENT);
        }
        if (icon == IntPtr.Zero && SHGetFileInfo(path, 0, ref info, size, SHGFI_ICON | SHGFI_LARGEICON) != IntPtr.Zero)
            icon = info.hIcon;
        if (icon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally { Win32.DestroyIcon(icon); }
    }

    const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_SYSICONINDEX = 0x4000;
    const int SHIL_EXTRALARGE = 2;
    const uint ILD_TRANSPARENT = 0x1;
    static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);
    // 返回的 IImageList 可以直接当 HIMAGELIST 用（文档明确允许）；系统共享列表，进程内只取一次，不释放。
    [DllImport("shell32.dll")]
    static extern int SHGetImageList(int list, ref Guid iid, out IntPtr imageList);
    [DllImport("comctl32.dll")]
    static extern IntPtr ImageList_GetIcon(IntPtr imageList, int index, uint flags);
}
