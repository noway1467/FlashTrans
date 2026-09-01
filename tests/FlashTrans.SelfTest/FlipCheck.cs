using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlashTrans.SelfTest;

/// <summary>
/// 判断一帧画面的上下方向。
///
/// 「视频倒过来了」这类 bug 用帧数、时长、盒子结构全查不出来——那些都对，
/// 只是每一行的顺序反了。唯一测得出来的办法是造一帧上下不对称的图，
/// 编完解回来看哪半亮。取平均亮度而不是比某个像素：H.264 压过之后
/// 单点会偏，半幅的均值不会。
/// </summary>
static class FlipCheck
{
    public static BitmapSource Load(string path)
    {
        using var fs = File.OpenRead(path);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return dec.Frames[0];
    }

    /// <summary>从 WinRT 的流里解一帧（GetThumbnailAsync 给的是 PNG/JPEG 字节流）。</summary>
    public static BitmapSource Decode(Windows.Storage.Streams.IRandomAccessStream stream)
    {
        using var net = stream.AsStreamForRead();
        var ms = new MemoryStream();
        net.CopyTo(ms);
        ms.Position = 0;
        var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return dec.Frames[0];
    }

    /// <summary>把 MP4 的第一帧解回来。</summary>
    public static async Task<BitmapSource> FirstMp4FrameAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
        var comp = new Windows.Media.Editing.MediaComposition();
        comp.Clips.Add(clip);
        using var stream = await comp.GetThumbnailAsync(
            TimeSpan.Zero, 0, 0, Windows.Media.Editing.VideoFramePrecision.NearestFrame);
        return Decode(stream);
    }

    /// <summary>
    /// 一帧四个角亮度都不一样的图：左上最亮，往右往下依次变暗。
    /// 上下翻转和左右镜像都能被认出来，只验上下会漏掉另一种。
    /// </summary>
    public static FlashTrans.Interop.CapturedImage Corners(int w, int h)
    {
        var buf = new byte[w * 4 * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // 左上 240、右上 160、左下 80、右下 0
                var v = (byte)((y < h / 2 ? 160 : 0) + (x < w / 2 ? 80 : 0));
                var o = (y * w + x) * 4;
                buf[o] = v; buf[o + 1] = v; buf[o + 2] = v; buf[o + 3] = 0xFF;
            }
        }
        return new FlashTrans.Interop.CapturedImage(w, h, buf);
    }

    /// <summary>四个角各自的平均亮度，顺序跟 <see cref="Corners"/> 一致。</summary>
    public static (double TL, double TR, double BL, double BR) Quadrants(BitmapSource img)
    {
        var (buf, stride, w, h) = Pixels(img);
        // 每个角取自己那块的中间部分，离接缝远一点：压缩过的边界会糊成中间色
        return (Mean(buf, stride, h / 6, h / 3, w / 6, w / 3),
                Mean(buf, stride, h / 6, h / 3, w * 2 / 3, w * 5 / 6),
                Mean(buf, stride, h * 2 / 3, h * 5 / 6, w / 6, w / 3),
                Mean(buf, stride, h * 2 / 3, h * 5 / 6, w * 2 / 3, w * 5 / 6));
    }

    /// <summary>上半和下半各自的平均亮度（0-255）。中间几行不算，边界压完会糊。</summary>
    public static (double Top, double Bottom) Halves(BitmapSource img)
    {
        var (buf, stride, w, h) = Pixels(img);
        // 各取自己那半的中间三分之一，离接缝远一点
        return (Mean(buf, stride, h / 6, h / 3, 0, w),
                Mean(buf, stride, h * 2 / 3, h * 5 / 6, 0, w));
    }

    static (byte[] Buf, int Stride, int W, int H) Pixels(BitmapSource img)
    {
        var bgra = img.Format == PixelFormats.Bgra32
            ? img
            : new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        var w = bgra.PixelWidth;
        var h = bgra.PixelHeight;
        var stride = w * 4;
        var buf = new byte[stride * h];
        bgra.CopyPixels(buf, stride, 0);
        return (buf, stride, w, h);
    }

    static double Mean(byte[] buf, int stride, int y0, int y1, int x0, int x1)
    {
        double sum = 0;
        var n = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x += 4)
            {
                var o = y * stride + x * 4;
                sum += (buf[o] + buf[o + 1] + buf[o + 2]) / 3.0;
                n++;
            }
        }
        return n == 0 ? 0 : sum / n;
    }
}
