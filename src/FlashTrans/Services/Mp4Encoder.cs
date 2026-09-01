using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlashTrans.Core;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FlashTrans.Services;

/// <summary>
/// 把一串 PNG 帧编成 MP4（H.264）。
///
/// 为什么这条不用外挂程序，而 WebP 那条要：H.264 编码器是系统自带的
/// （Media Foundation 里的 mfh264enc），WinRT 那层 Windows.Media.Transcoding
/// 直接就能用上，随包体积增加 0 字节。WebP 恰好相反——系统只带解码器。
///
/// 走的是 MediaStreamSource 而不是 MediaComposition：后者要把每帧当成一个
/// MediaClip，300 帧就是 300 个 clip，而且每帧的时长只能按「图片停留时间」给，
/// 拿不到精确的时间戳。MediaStreamSource 这条是自己按帧喂样本，时间戳我们说了算。
///
/// 帧数据是 BGRA8 未压缩样本，转码器负责压成 H.264。一帧 1920×1080 是 8MB，
/// 所以一次只解一帧、喂完就扔，不预先解全部。
/// </summary>
public static class Mp4Encoder
{
    /// <summary>系统 H.264 编码器那个 DLL。用来在设置页里提前说一句「这台机器行不行」。</summary>
    static readonly string EncoderDll = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "mfh264enc.dll");

    /// <summary>
    /// 这台机器有没有系统 H.264 编码器。
    /// 只是给界面提示用的粗判断——真正算不算数还是看编那一下成不成，
    /// 所以 SaveAsync 失败时照样有退回 WebP 那条路。
    /// </summary>
    public static bool Available
    {
        get { try { return File.Exists(EncoderDll); } catch { return false; } }
    }

    /// <summary>
    /// 把边长吸到 4 的倍数。
    ///
    /// 4:2:0 色度采样只要求偶数，但系统这个 H.264 编码器实际要 4 的倍数：
    /// 差 2 的时候 CanTranscode 仍然是 true，TranscodeAsync 才抛 COMException
    /// 0x80004005「未指定的错误」，看起来就像「这台机器编不了 MP4」。
    ///
    /// 实测（`--mp4lab` 按边长扫的，宽高两边一样）：
    /// 636 成、638 败、640 成、642 败、644 成；476 成、478 败、480 成、482 败。
    /// 1366×768 正好撞上——所以用户全屏宽度录一段必错，而自测那些 48×32、
    /// 640×480 的合成帧全是 4 的倍数，一路都是绿的。
    ///
    /// 吸下去最多切掉 3 行/列像素，比整段录制废掉划算。
    /// </summary>
    internal static int Align4(int v) => v - (v & 3);

    /// <summary>
    /// 每像素每帧给多少 bit。屏幕内容不像实拍视频：大片纯色加细小的文字边缘，
    /// 给低了首先烂的是字。这个值是实测调出来的，见 Bitrate。
    /// </summary>
    internal static double BitsPerPixel = 0.12;

    /// <summary>只给量尺子的工具用：直接指定码率，跳过上面那套折算和夹取。</summary>
    internal static uint? ForceBitrate;

    /// <summary>
    /// 码率。按像素数乘帧率折算，再夹到一个合理区间。
    /// </summary>
    internal static uint Bitrate(int w, int h, int fps)
        => (uint)Math.Clamp((long)(w * h * fps * BitsPerPixel), 800_000, 40_000_000);

    /// <summary>
    /// 转码时先写的那个旁路文件，把「临时」放在文件名里而不是后缀上：
    /// `闪译录制 ….part.mp4`。
    ///
    /// 保住 .mp4 后缀是防御性的：Media Foundation 给 StorageFile 挑写出器（sink）
    /// 参考扩展名，`.part` 结尾理论上可能挑不到 MP4 sink。实测这台机器上
    /// `x.mp4.part` 也能编（0x80004005 的真因是边长不是 4 的倍数，见 Align4），
    /// 但后缀跟容器对得上总是更稳，也让半成品文件双击能放。
    /// </summary>
    internal static string SidecarPath(string finalPath)
    {
        var dir = Path.GetDirectoryName(finalPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(finalPath);
        var ext = Path.GetExtension(finalPath);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";
        return Path.Combine(dir, stem + ".part" + ext);
    }

    public static async Task<AnimResult> SaveAsync(
        IReadOnlyList<string> frames, string outPath, int fps)
    {
        if (frames.Count == 0) throw new InvalidOperationException("没有帧可以编码。");
        fps = Math.Max(1, fps);
        var finalPath = Path.GetFullPath(outPath);
        var tempPath = SidecarPath(finalPath);

        try
        {
            // 拿第一帧定分辨率。录制中区域不变，所有帧一样大。
            var first = AnimEncoder.LoadFrame(frames[0]);
            var w = Align4(first.PixelWidth);
            var h = Align4(first.PixelHeight);
            if (w < 2 || h < 2)
                throw new InvalidOperationException($"区域太小，编不了 MP4（{w}×{h}）。");

            var descriptor = new VideoStreamDescriptor(
                VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)w, (uint)h));

            var source = new MediaStreamSource(descriptor)
            {
                // 帧是现成的，不是实时流。BufferTime 留着的话转码器会先等一段。
                BufferTime = TimeSpan.Zero,
                Duration = TimeSpan.FromSeconds((double)frames.Count / fps),
            };

            var frameDuration = TimeSpan.FromSeconds(1.0 / fps);
            var next = 0;
            Exception? failure = null;

            source.SampleRequested += (_, args) =>
            {
                var req = args.Request;
                if (next >= frames.Count)
                {
                    // 不给样本就是流结束了。必须显式走这一步，不然转码器一直等。
                    req.Sample = null;
                    return;
                }
                try
                {
                    var i = next++;
                    req.Sample = MediaStreamSample.CreateFromBuffer(
                        ToBuffer(frames[i], w, h), frameDuration * i);
                    req.Sample.Duration = frameDuration;
                    // 每帧都当关键帧候选：屏幕录制常要来回拖进度条。
                    req.Sample.KeyFrame = i == 0;
                }
                catch (Exception ex)
                {
                    // 这个回调是转码器在自己的线程上调的，抛出去就没人接了，
                    // 表现是转码莫名其妙地成功但文件是残的。记下来，等外面统一报。
                    failure ??= ex;
                    req.Sample = null;
                }
            };

            var bitrate = ForceBitrate ?? Bitrate(w, h, fps);

            // 视频属性自己建，不走 CreateMp4(quality) 那套预设：预设会按它自己的档位
            // 填好宽高帧率码率，事后改 profile.Video 的字段不一定被采纳。
            var profile = new MediaEncodingProfile { Container = new ContainerEncodingProperties() };
            profile.Container.Subtype = MediaEncodingSubtypes.Mpeg4;
            profile.Audio = null;   // 只录画面，不录声音

            var video = VideoEncodingProperties.CreateH264();
            video.Width = (uint)w;
            video.Height = (uint)h;
            video.Bitrate = bitrate;
            video.FrameRate.Numerator = (uint)fps;
            video.FrameRate.Denominator = 1;
            video.PixelAspectRatio.Numerator = 1;
            video.PixelAspectRatio.Denominator = 1;
            profile.Video = video;

            // 先写旁路临时文件。转码半路失败时，用户目录里不能出现 0 字节的假 MP4。
            var dir = Path.GetDirectoryName(finalPath)
                ?? throw new InvalidOperationException("输出路径没有目录部分。");
            Directory.CreateDirectory(dir);
            var folder = await StorageFolder.GetFolderFromPathAsync(dir);
            var file = await folder.CreateFileAsync(
                Path.GetFileName(tempPath), CreationCollisionOption.ReplaceExisting);

            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
                var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                    source, stream, profile);
                if (!prepared.CanTranscode)
                    throw new InvalidOperationException(
                        $"系统拒绝转码（{prepared.FailureReason}）。这台机器可能没装 H.264 编码器。");

                await prepared.TranscodeAsync();
            }

            if (failure is not null)
                throw new InvalidOperationException("喂帧的时候出错了：" + failure.Message, failure);

            var info = new FileInfo(tempPath);
            if (!info.Exists || info.Length == 0)
                throw new InvalidOperationException("转码跑完了但文件是空的。");

            File.Move(tempPath, finalPath, overwrite: true);
            return new AnimResult(finalPath, RecordFormat.Mp4, info.Length);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception cleanupEx)
            {
                Log.Warn("清理 MP4 临时文件失败：" + cleanupEx.Message);
            }
            throw;
        }
    }

    /// <summary>
    /// 读一帧 PNG，转成紧排的 BGRA8 塞进 IBuffer，行序是从下往上的。
    ///
    /// 为什么要倒着写：Media Foundation 里未压缩的 RGB 格式默认是「自底向上」
    /// （bottom-up，跟 GDI 的 DIB 一个传统），而 PNG 和我们的 CapturedImage 都是
    /// 自顶向下。直接喂进去，编出来的视频整个上下颠倒——帧数、时长、盒子结构
    /// 全是对的，只有行序反了，所以光验容器结构查不出来。`--fliplab` 的实测：
    /// 源帧四个角亮度 240/160/80/0，不翻的话解回来是 80/0/240/160——上下换了，
    /// 左右没换，正是纯行序倒置。
    ///
    /// 另一条路是把 VideoEncodingProperties 的 stride 设成负数来声明自顶向下，
    /// 但 WinRT 那层投影没有暴露这个字段（MF_MT_DEFAULT_STRIDE 只能在原生
    /// IMFMediaType 上设），所以只能在这儿把行倒过来。一次多拷一行的内存。
    ///
    /// 走 DataWriter 而不是 LockBuffer + IMemoryBufferByteAccess：后者在 .NET 5+
    /// 上会抛 InvalidCastException，CsWinRT 的对象不是真 COM RCW，转不过去。
    /// 跟 OcrService.ToSoftwareBitmap 一个道理。
    /// </summary>
    static IBuffer ToBuffer(string path, int w, int h)
    {
        var src = AnimEncoder.LoadFrame(path);
        // 抓屏出来本来就是 BGRA32，但 PNG 存回来可能被写成别的（比如没有 alpha 的 24 位）。
        BitmapSource bgra = src.Format == PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        var stride = w * 4;
        var bytes = new byte[stride * h];
        // 只取左上 w×h：吸到 4 的倍数可能比原图各少几个像素。
        bgra.CopyPixels(new System.Windows.Int32Rect(0, 0, w, h), bytes, stride, 0);
        FlipRows(bytes, stride, h);

        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    /// <summary>把 h 行像素首尾对调，原地翻转上下。</summary>
    internal static void FlipRows(byte[] bytes, int stride, int h)
    {
        var row = new byte[stride];
        // BlockCopy 前面要写 System.：这个文件 using 了 Windows.Storage.Streams，
        // 那里面也有个 Buffer 类，不限定的话是二义引用。
        for (var y = 0; y < h / 2; y++)
        {
            var top = y * stride;
            var bottom = (h - 1 - y) * stride;
            System.Buffer.BlockCopy(bytes, top, row, 0, stride);
            System.Buffer.BlockCopy(bytes, bottom, bytes, top, stride);
            System.Buffer.BlockCopy(row, 0, bytes, bottom, stride);
        }
    }
}
