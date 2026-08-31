using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlashTrans.Interop;

/// <summary>一块抓下来的屏幕像素。BGRA32，逐行紧密排列（Stride = Width * 4）。</summary>
public sealed class CapturedImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;
    public int Stride => Width * 4;

    public BitmapSource ToBitmap()
    {
        var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Pixels, Stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>存成 PNG。目录不存在会自己建。</summary>
    public void SavePng(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(ToBitmap()));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    /// <summary>放大到至少这么高，给 OCR 用。小字号直接识别容易漏字。</summary>
    public CapturedImage ScaleUpTo(int minHeight, int maxScale = 4)
    {
        if (Height <= 0 || Height >= minHeight) return this;
        var factor = Math.Min(maxScale, (int)Math.Ceiling((double)minHeight / Height));
        if (factor <= 1) return this;

        var src = ToBitmap();
        var scaled = new TransformedBitmap(src, new ScaleTransform(factor, factor));
        var w = scaled.PixelWidth;
        var h = scaled.PixelHeight;
        var stride = w * 4;
        var buf = new byte[stride * h];
        scaled.CopyPixels(buf, stride, 0);
        return new CapturedImage(w, h, buf);
    }

    /// <summary>
    /// 打上马赛克：每 block×block 个像素取平均，整块涂成那个平均色。
    ///
    /// 自己算而不是「缩小再放大」——缩放这条路要过 WPF 两道重采样，
    /// 想让它出硬边得处处挂对 NearestNeighbor，漏一处就糊成一片。
    /// 马赛克是用来遮东西的，糊而不碎意味着字还认得出来，那就等于没遮。
    /// </summary>
    public CapturedImage Mosaic(int block)
    {
        block = Math.Max(2, block);
        var outBuf = new byte[Pixels.Length];

        for (var by = 0; by < Height; by += block)
        {
            var yEnd = Math.Min(by + block, Height);
            for (var bx = 0; bx < Width; bx += block)
            {
                var xEnd = Math.Min(bx + block, Width);

                // 一块里所有像素加起来求平均。用 long 装：一块最大 40×40，
                // 单通道最多 1600×255，int 也够，但求和累加习惯上留出余量。
                long b = 0, g = 0, r = 0, a = 0;
                for (var y = by; y < yEnd; y++)
                {
                    var row = y * Stride;
                    for (var x = bx; x < xEnd; x++)
                    {
                        var i = row + x * 4;
                        b += Pixels[i];
                        g += Pixels[i + 1];
                        r += Pixels[i + 2];
                        a += Pixels[i + 3];
                    }
                }

                var n = (xEnd - bx) * (yEnd - by);
                var cb = (byte)(b / n);
                var cg = (byte)(g / n);
                var cr = (byte)(r / n);
                var ca = (byte)(a / n);

                for (var y = by; y < yEnd; y++)
                {
                    var row = y * Stride;
                    for (var x = bx; x < xEnd; x++)
                    {
                        var i = row + x * 4;
                        outBuf[i] = cb;
                        outBuf[i + 1] = cg;
                        outBuf[i + 2] = cr;
                        outBuf[i + 3] = ca;
                    }
                }
            }
        }

        return new CapturedImage(Width, Height, outBuf);
    }
}

/// <summary>用 GDI 抓屏。坐标是物理像素（本程序声明了 per-monitor v2，不会被系统虚拟化）。</summary>
public static class ScreenCapture
{
    /// <summary>整个虚拟桌面的物理像素范围，多显示器时含负坐标。</summary>
    public static RECT VirtualScreen()
    {
        var x = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        var y = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        return new RECT
        {
            Left = x,
            Top = y,
            Right = x + Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN),
            Bottom = y + Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN),
        };
    }

    /// <summary>抓一块屏幕。失败或宽高为 0 时返回 null。</summary>
    public static CapturedImage? Grab(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        var screen = Win32.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;

        var mem = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var old = IntPtr.Zero;
        try
        {
            mem = Win32.CreateCompatibleDC(screen);
            if (mem == IntPtr.Zero) return null;

            // 自上而下（负高度）+ 32 位，拿到的就是 BGRA 紧密排列，不用再翻转和补齐
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Win32.BI_RGB,
                }
            };
            dib = Win32.CreateDIBSection(screen, ref info, Win32.DIB_RGB_COLORS, out var bits,
                                         IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;

            old = Win32.SelectObject(mem, dib);
            // CAPTUREBLT 会把分层窗口（部分输入法候选框、半透明窗口）也算进去
            if (!Win32.BitBlt(mem, 0, 0, width, height, screen, x, y,
                              Win32.SRCCOPY | Win32.CAPTUREBLT))
                return null;

            // GDI 的绘制可能还在队列里，读像素前先等它落地
            Win32.GdiFlush();

            var buf = new byte[width * 4 * height];
            Marshal.Copy(bits, buf, 0, buf.Length);

            // 屏幕内容不透明，但 DIB 的 alpha 通道是 BitBlt 没管的垃圾值。
            // 不铺成 255，后面按 Bgra8 交给 OCR 会得到一张「全透明」的图，识别结果为空。
            for (var i = 3; i < buf.Length; i += 4) buf[i] = 0xFF;

            return new CapturedImage(width, height, buf);
        }
        finally
        {
            if (old != IntPtr.Zero) Win32.SelectObject(mem, old);
            if (dib != IntPtr.Zero) Win32.DeleteObject(dib);
            if (mem != IntPtr.Zero) Win32.DeleteDC(mem);
            Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static CapturedImage? Grab(RECT r) => Grab(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>WPF 单位的矩形换成物理像素。选区窗口给出的是 DIP。</summary>
    public static RECT ToPixels(Rect dip, double scaleX, double scaleY) => new()
    {
        Left = (int)Math.Round(dip.Left * scaleX),
        Top = (int)Math.Round(dip.Top * scaleY),
        Right = (int)Math.Round(dip.Right * scaleX),
        Bottom = (int)Math.Round(dip.Bottom * scaleY),
    };
}
