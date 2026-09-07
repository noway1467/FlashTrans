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

    /// <summary>给 OCR 用的整数倍最近邻放大，避免细笔画被插值抹掉。</summary>
    public CapturedImage ScaleUpForOcr(int minHeight = 640, int maxScale = 8,
                                      int maxDimension = 4096)
    {
        if (Width <= 0 || Height <= 0) return this;
        var factor = Math.Min(maxScale, (int)Math.Ceiling((double)minHeight / Height));
        var longest = Math.Max(Width, Height);
        if (longest > 0) factor = Math.Min(factor, Math.Max(1, maxDimension / longest));
        if (factor <= 1) return this;

        var w = checked(Width * factor);
        var h = checked(Height * factor);
        var stride = w * 4;
        var buf = new byte[stride * h];
        for (var y = 0; y < h; y++)
        {
            var srcRow = (y / factor) * Stride;
            var dstRow = y * stride;
            for (var x = 0; x < w; x++)
            {
                var src = srcRow + (x / factor) * 4;
                var dst = dstRow + x * 4;
                buf[dst] = Pixels[src];
                buf[dst + 1] = Pixels[src + 1];
                buf[dst + 2] = Pixels[src + 2];
                buf[dst + 3] = 0xFF;
            }
        }
        return new CapturedImage(w, h, buf);
    }

    /// <summary>给紧裁切文字加少量同背景留白，避免首尾笔画被当作图像边界丢弃。</summary>
    internal CapturedImage PadForOcr(int padding = 16, int maxDimension = 4096)
    {
        if (Width <= 0 || Height <= 0) return this;
        padding = Math.Min(padding, (maxDimension - Math.Max(Width, Height)) / 2);
        if (padding <= 0) return this;

        // 用边缘像素的亮度中位数估计背景，深色截图也不会被强行补成白边。
        var edges = new List<int>();
        var step = Math.Max(1, Math.Min(Width, Height) / 16);
        void Add(int x, int y)
        {
            var i = y * Stride + x * 4;
            edges.Add(Pixels[i] | Pixels[i + 1] << 8 | Pixels[i + 2] << 16);
        }
        for (var x = 0; x < Width; x += step) { Add(x, 0); Add(x, Height - 1); }
        for (var y = 0; y < Height; y += step) { Add(0, y); Add(Width - 1, y); }
        var color = edges.OrderBy(c => Luma((byte)c, (byte)(c >> 8), (byte)(c >> 16)))
            .ElementAt(edges.Count / 2);
        var w = checked(Width + padding * 2);
        var h = checked(Height + padding * 2);
        var pixels = new byte[checked(w * h * 4)];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)color;
            pixels[i + 1] = (byte)(color >> 8);
            pixels[i + 2] = (byte)(color >> 16);
            pixels[i + 3] = 255;
        }
        for (var y = 0; y < Height; y++)
            Buffer.BlockCopy(Pixels, y * Stride, pixels, ((y + padding) * w + padding) * 4, Stride);
        return new CapturedImage(w, h, pixels);
    }

    /// <summary>保留抗锯齿的补充通道；每一步都受 Windows OCR 最大边长约束。</summary>
    internal CapturedImage SmoothForOcr(int maxDimension = 4096)
    {
        if (Width <= 0 || Height <= 0) return this;
        var scale = Math.Min(4, maxDimension / Math.Max(Width, Height));
        return ScaleUpTo(320, scale).PadForOcr(16, maxDimension)
            .ScaleUpForOcr(640, 8, maxDimension);
    }

    /// <summary>按 90 度转动像素，不做插值，也不改原截图。</summary>
    internal CapturedImage RotateForOcr(bool clockwise)
    {
        var pixels = new byte[Pixels.Length];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var source = y * Stride + x * 4;
                var dx = clockwise ? Height - y - 1 : y;
                var dy = clockwise ? x : Width - x - 1;
                var dest = (dy * Height + dx) * 4;
                pixels[dest] = Pixels[source];
                pixels[dest + 1] = Pixels[source + 1];
                pixels[dest + 2] = Pixels[source + 2];
                pixels[dest + 3] = Pixels[source + 3];
            }
        return new CapturedImage(Height, Width, pixels);
    }

    /// <summary>生成适合 OCR 的灰度高对比版本。</summary>
    public CapturedImage EnhanceForOcr()
    {
        if (Width <= 0 || Height <= 0 || Pixels.Length == 0) return this;
        byte min = 255, max = 0;
        for (var i = 0; i + 3 < Pixels.Length; i += 4)
        {
            var gray = Luma(Pixels[i], Pixels[i + 1], Pixels[i + 2]);
            if (gray < min) min = gray;
            if (gray > max) max = gray;
        }
        var range = max - min;
        var buf = new byte[Pixels.Length];
        for (var i = 0; i + 3 < Pixels.Length; i += 4)
        {
            var gray = Luma(Pixels[i], Pixels[i + 1], Pixels[i + 2]);
            var v = range >= 32 ? (gray - min) * 255 / range : gray;
            var b = (byte)Math.Clamp(v, 0, 255);
            buf[i] = b;
            buf[i + 1] = b;
            buf[i + 2] = b;
            buf[i + 3] = 0xFF;
        }
        return new CapturedImage(Width, Height, buf);
    }

    /// <summary>把图像转成 Otsu 黑白图，补救低对比度的括号、冒号和下划线。</summary>
    public CapturedImage BinarizeForOcr()
    {
        if (Width <= 0 || Height <= 0 || Pixels.Length == 0) return this;
        var histogram = new int[256];
        for (var i = 0; i + 3 < Pixels.Length; i += 4)
            histogram[Luma(Pixels[i], Pixels[i + 1], Pixels[i + 2])]++;

        var total = Width * Height;
        long weighted = 0;
        for (var i = 0; i < histogram.Length; i++) weighted += (long)i * histogram[i];
        long weight = 0, sum = 0;
        var best = -1.0;
        var threshold = 127;
        for (var i = 0; i < histogram.Length; i++)
        {
            weight += histogram[i];
            if (weight == 0) continue;
            var other = total - weight;
            if (other == 0) break;
            sum += (long)i * histogram[i];
            var meanA = (double)sum / weight;
            var meanB = (double)(weighted - sum) / other;
            var variance = (double)weight * other * (meanA - meanB) * (meanA - meanB);
            if (variance > best) { best = variance; threshold = i; }
        }

        var buf = new byte[Pixels.Length];
        for (var i = 0; i + 3 < Pixels.Length; i += 4)
        {
            var value = Luma(Pixels[i], Pixels[i + 1], Pixels[i + 2]) <= threshold ? (byte)0 : (byte)255;
            buf[i] = value;
            buf[i + 1] = value;
            buf[i + 2] = value;
            buf[i + 3] = 0xFF;
        }
        return new CapturedImage(Width, Height, buf);
    }

    static byte Luma(byte b, byte g, byte r) =>
        (byte)((19 * b + 183 * g + 54 * r + 128) >> 8);

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

/// <summary>一组可复用的 GDI 抓屏资源。适合连续录制同一块固定区域。</summary>
public sealed class ScreenCaptureSession : IDisposable
{
    readonly int _x;
    readonly int _y;
    readonly int _width;
    readonly int _height;
    readonly IntPtr _screen;
    readonly IntPtr _memory;
    readonly IntPtr _dib;
    readonly IntPtr _bits;
    readonly IntPtr _old;
    bool _disposed;

    ScreenCaptureSession(int x, int y, int width, int height,
                         IntPtr screen, IntPtr memory, IntPtr dib,
                         IntPtr bits, IntPtr old)
    {
        _x = x;
        _y = y;
        _width = width;
        _height = height;
        _screen = screen;
        _memory = memory;
        _dib = dib;
        _bits = bits;
        _old = old;
    }

    public static ScreenCaptureSession? Open(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        var screen = Win32.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;

        var memory = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var old = IntPtr.Zero;
        var opened = false;
        try
        {
            memory = Win32.CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) return null;

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
            dib = Win32.CreateDIBSection(screen, ref info, Win32.DIB_RGB_COLORS,
                                         out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;

            old = Win32.SelectObject(memory, dib);
            if (old == IntPtr.Zero) return null;

            var session = new ScreenCaptureSession(x, y, width, height,
                                                   screen, memory, dib, bits, old);
            opened = true;
            return session;
        }
        finally
        {
            if (!opened && memory != IntPtr.Zero)
            {
                if (old != IntPtr.Zero) Win32.SelectObject(memory, old);
                if (dib != IntPtr.Zero) Win32.DeleteObject(dib);
                Win32.DeleteDC(memory);
            }
            if (!opened) Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public CapturedImage? Grab()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ScreenCaptureSession));
        if (!Win32.BitBlt(_memory, 0, 0, _width, _height, _screen, _x, _y,
                          Win32.SRCCOPY | Win32.CAPTUREBLT))
            return null;

        Win32.GdiFlush();
        var buf = GC.AllocateUninitializedArray<byte>(_width * 4 * _height);
        Marshal.Copy(_bits, buf, 0, buf.Length);
        for (var i = 3; i < buf.Length; i += 4) buf[i] = 0xFF;
        return new CapturedImage(_width, _height, buf);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_old != IntPtr.Zero) Win32.SelectObject(_memory, _old);
        if (_dib != IntPtr.Zero) Win32.DeleteObject(_dib);
        if (_memory != IntPtr.Zero) Win32.DeleteDC(_memory);
        if (_screen != IntPtr.Zero) Win32.ReleaseDC(IntPtr.Zero, _screen);
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
        using var session = ScreenCaptureSession.Open(x, y, width, height);
        return session?.Grab();
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
