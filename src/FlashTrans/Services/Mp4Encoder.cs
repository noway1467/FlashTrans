using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlashTrans.Core;
using FlashTrans.Interop;
using Mf = FlashTrans.Interop.MediaFoundation;

namespace FlashTrans.Services;

/// <summary>逐帧喂给系统 Media Foundation，以 H.264 编码；可在同一 MP4 中写入 AAC 音轨。</summary>
public static class Mp4Encoder
{
    static readonly string EncoderDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mfh264enc.dll");

    /// <summary>只用于界面提示，实际可用性仍由编码器初始化决定。</summary>
    public static bool Available { get { try { return File.Exists(EncoderDll); } catch { return false; } } }

    /// <summary>沿用已验证的 4 像素对齐；系统 H.264 的边长只为偶数时也曾转码失败。</summary>
    internal static int Align4(int value) => value - (value & 3);
    internal static double BitsPerPixel = 0.12;
    internal static uint? ForceBitrate;
    internal static uint Bitrate(int w, int h, int fps) =>
        (uint)Math.Clamp((long)((double)w * h * fps * BitsPerPixel), 800_000, 40_000_000);

    /// <summary>保留容器后缀供系统选择 MP4 sink；编码成功前不改最终文件。</summary>
    internal static string SidecarPath(string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath) ?? "";
        var extension = Path.GetExtension(finalPath);
        if (string.IsNullOrEmpty(extension)) extension = ".mp4";
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(finalPath) + ".part" + extension);
    }

    public static Task<AnimResult> SaveAsync(IReadOnlyList<string> frames, string outPath, int fps,
        string? audioPath = null, CancellationToken ct = default)
    {
        if (frames.Count == 0) throw new InvalidOperationException("没有帧可以编码。");
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => Save(frames, outPath, Math.Max(1, fps), audioPath, ct), ct);
    }

    static AnimResult Save(IReadOnlyList<string> frames, string outPath, int fps, string? audioPath, CancellationToken ct)
    {
        using var apartment = new NativeWinRt.Apartment();
        using var foundation = new Mf.Lifetime();
        var finalPath = Path.GetFullPath(outPath);
        var temporary = SidecarPath(finalPath);
        try
        {
            ct.ThrowIfCancellationRequested();
            var first = AnimEncoder.LoadFrame(frames[0]);
            var width = Align4(first.PixelWidth);
            var height = Align4(first.PixelHeight);
            if (width < 4 || height < 4) throw new InvalidOperationException($"区域太小，编不了 MP4（{width}×{height}）。");
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            if (!string.IsNullOrEmpty(audioPath) && File.Exists(audioPath))
            {
                try
                {
                    using var audio = new PcmWave(audioPath);
                    Encode(frames, temporary, width, height, fps, audio, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ct.ThrowIfCancellationRequested();
                    Log.Warn("音视频合成失败，保存纯视频文件：" + ex.Message);
                    File.Delete(temporary);
                    Encode(frames, temporary, width, height, fps, null, ct);
                }
            }
            else Encode(frames, temporary, width, height, fps, null, ct);
            ct.ThrowIfCancellationRequested();
            var bytes = new FileInfo(temporary).Length;
            if (bytes == 0) throw new InvalidOperationException("转码跑完了但文件是空的。");
            File.Move(temporary, finalPath, overwrite: true);
            return new AnimResult(finalPath, RecordFormat.Mp4, bytes);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (Exception ex) { Log.Warn("清理 MP4 临时文件失败：" + ex.Message); }
            throw;
        }
    }

    static void Encode(IReadOnlyList<string> frames, string path, int width, int height, int fps,
        PcmWave? audio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var writer = Mf.SinkWriter(path);
        using var videoOutput = VideoType(Mf.H264, width, height, fps);
        Mf.Set(videoOutput, Mf.AverageBitrate, ForceBitrate ?? Bitrate(width, height, fps));
        var videoStream = Mf.AddStream(writer, videoOutput);
        using var videoInput = VideoType(Mf.Rgb32, width, height, fps);
        Mf.Set(videoInput, Mf.DefaultStride, (uint)(width * 4));
        Mf.SetInput(writer, videoStream, videoInput);
        uint audioStream = 0;
        if (audio is not null)
        {
            using var audioOutput = Mf.MediaType();
            Mf.Set(audioOutput, Mf.MajorType, Mf.Audio);
            Mf.Set(audioOutput, Mf.Subtype, Mf.Aac);
            // Windows AAC 支持 32/44.1/48kHz；其他 PCM 采样率由 sink 的转换器重采样。
            var outputRate = audio.SampleRate is 32000 or 44100 or 48000 ? audio.SampleRate : 48000u;
            Mf.Set(audioOutput, Mf.SampleRate, outputRate);
            Mf.Set(audioOutput, Mf.Channels, Math.Min(audio.Channels, 2u));
            Mf.Set(audioOutput, Mf.BitsPerSample, 16u);
            Mf.Set(audioOutput, Mf.AudioBytesPerSecond, 20_000u);
            Mf.Set(audioOutput, new Guid("bfbabe79-7434-4d1c-94f0-72a3b9e17188"), 0u);
            Mf.Set(audioOutput, new Guid("7632f0e6-9538-4d61-acda-ea29c8c14456"), 0x29u);
            audioStream = Mf.AddStream(writer, audioOutput);
            using var audioInput = Mf.MediaType();
            Mf.Set(audioInput, Mf.MajorType, Mf.Audio);
            Mf.Set(audioInput, Mf.Subtype, Mf.Pcm);
            Mf.Set(audioInput, Mf.SampleRate, audio.SampleRate);
            Mf.Set(audioInput, Mf.Channels, audio.Channels);
            Mf.Set(audioInput, Mf.BitsPerSample, 16u);
            Mf.Set(audioInput, Mf.BlockAlignment, audio.Channels * 2);
            Mf.Set(audioInput, Mf.AudioBytesPerSecond, checked(audio.SampleRate * audio.Channels * 2));
            Mf.SetInput(writer, audioStream, audioInput);
        }
        Mf.Begin(writer);
        var totalTime = frames.Count * 10_000_000L / fps;
        long audioFrames = 0;
        for (var frame = 0; frame < frames.Count; frame++)
        {
            ct.ThrowIfCancellationRequested();
            var time = frame * 10_000_000L / fps;
            var nextTime = (frame + 1L) * 10_000_000L / fps;
            var pixels = FramePixels(frames[frame], width, height);
            ct.ThrowIfCancellationRequested();
            Mf.Write(writer, videoStream, pixels, pixels.Length, time, nextTime - time);
            if (audio is null) continue;
            var maximumFrames = Math.Min(audio.TotalFrames, totalTime * audio.SampleRate / 10_000_000);
            while (audioFrames < maximumFrames && audioFrames * 10_000_000L / audio.SampleRate < nextTime)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(1024, maximumFrames - audioFrames);
                var bytes = audio.ReadFrames(count);
                var audioTime = audioFrames * 10_000_000L / audio.SampleRate;
                audioFrames += count;
                var duration = audioFrames * 10_000_000L / audio.SampleRate - audioTime;
                Mf.Write(writer, audioStream, bytes, bytes.Length, audioTime, duration);
            }
        }
        ct.ThrowIfCancellationRequested();
        Mf.Finish(writer);
        ct.ThrowIfCancellationRequested();
    }

    static NativeCom VideoType(Guid subtype, int width, int height, int fps)
    {
        var type = Mf.MediaType();
        try
        {
            Mf.Set(type, Mf.MajorType, Mf.Video);
            Mf.Set(type, Mf.Subtype, subtype);
            Mf.Set(type, Mf.FrameSize, Mf.Pair((uint)width, (uint)height));
            Mf.Set(type, Mf.FrameRate, Mf.Pair((uint)fps, 1));
            Mf.Set(type, Mf.PixelAspectRatio, Mf.Pair(1, 1));
            Mf.Set(type, Mf.InterlaceMode, 2u);
            return type;
        }
        catch { type.Dispose(); throw; }
    }

    static byte[] FramePixels(string path, int width, int height)
    {
        var source = AnimEncoder.LoadFrame(path);
        BitmapSource bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(width * 4);
        var bytes = new byte[checked(stride * height)];
        bgra.CopyPixels(new System.Windows.Int32Rect(0, 0, width, height), bytes, stride, 0);
        // RGB32 输入仍使用 DIB 的 bottom-up 布局，保留原来已经实测过的翻行行为。
        FlipRows(bytes, stride, height);
        return bytes;
    }

    internal static void FlipRows(byte[] bytes, int stride, int h)
    {
        var row = new byte[stride];
        for (var y = 0; y < h / 2; y++)
        {
            var top = y * stride;
            var bottom = (h - 1 - y) * stride;
            Buffer.BlockCopy(bytes, top, row, 0, stride);
            Buffer.BlockCopy(bytes, bottom, bytes, top, stride);
            Buffer.BlockCopy(row, 0, bytes, bottom, stride);
        }
    }

    /// <summary>流式读取 RIFF PCM，避免整段录音常驻；8/24/32 位整数 PCM 转为 AAC 接受的 16 位。</summary>
    sealed class PcmWave : IDisposable
    {
        readonly FileStream _stream;
        readonly int _bits;
        readonly int _alignment;
        public uint Channels { get; }
        public uint SampleRate { get; }
        public long TotalFrames { get; }

        public PcmWave(string path)
        {
            _stream = File.OpenRead(path);
            try
            {
                using var reader = new BinaryReader(_stream, Encoding.ASCII, leaveOpen: true);
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("音频不是 RIFF WAV。");
                var riffEnd = Math.Min(_stream.Length, reader.ReadUInt32() + 8L);
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("音频不是 WAV。");
                byte[]? format = null;
                long dataOffset = 0;
                long dataLength = 0;
                while (_stream.Position + 8 <= riffEnd)
                {
                    var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    var length = reader.ReadUInt32();
                    var start = _stream.Position;
                    if (length > riffEnd - start) throw new InvalidDataException("WAV 数据块超过文件范围。");
                    if (id == "fmt ")
                    {
                        if (length < 16 || length > 4096) throw new InvalidDataException("WAV 格式块无效。");
                        format = reader.ReadBytes((int)length);
                    }
                    else if (id == "data" && dataOffset == 0) { dataOffset = start; dataLength = length; }
                    _stream.Position = start + length + (length & 1);
                }
                if (format is null || dataOffset == 0) throw new InvalidDataException("WAV 缺少格式或音频数据。");
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
                if (tag == 0xfffe && format.Length >= 40)
                {
                    if (new Guid(format.AsSpan(24, 16)) != Mf.Pcm) throw new InvalidDataException("WAV 扩展格式不是整数 PCM。");
                    tag = 1;
                }
                if (tag != 1) throw new InvalidDataException("音频需要整数 PCM WAV。");
                Channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2));
                SampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
                _alignment = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12));
                _bits = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14));
                if (Channels is 0 or > 8 || SampleRate is 0 or > 384000 || _bits is not (8 or 16 or 24 or 32)
                    || _alignment != Channels * (_bits / 8) || dataLength % _alignment != 0)
                    throw new InvalidDataException("PCM 音频参数或数据长度无效。");
                TotalFrames = dataLength / _alignment;
                if (TotalFrames == 0) throw new InvalidDataException("WAV 没有音频样本。");
                _stream.Position = dataOffset;
            }
            catch { _stream.Dispose(); throw; }
        }

        public byte[] ReadFrames(int frames)
        {
            var input = new byte[checked(frames * _alignment)];
            _stream.ReadExactly(input);
            if (_bits == 16) return input;
            var output = new byte[checked(frames * (int)Channels * 2)];
            var sampleBytes = _bits / 8;
            for (var source = 0; source < input.Length; source += sampleBytes)
            {
                var destination = source / sampleBytes * 2;
                short sample = _bits switch
                {
                    8 => (short)((input[source] - 128) << 8),
                    24 => BinaryPrimitives.ReadInt16LittleEndian(input.AsSpan(source + 1)),
                    _ => (short)(BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(source)) >> 16),
                };
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(destination), sample);
            }
            return output;
        }

        public void Dispose() => _stream.Dispose();
    }
}
