using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FlashTrans.Services;

/// <summary>
/// Captures speaker output through WASAPI render-loopback, never microphone input.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    const uint ClsctxAll = 0x17;
    const uint StreamFlagLoopback = 0x0002_0000;
    const uint BufferFlagSilent = 0x0000_0002;
    const int ShareModeShared = 0;
    const uint AudioRoleMultimedia = 1;
    const ushort FormatPcm = 1;
    const ushort FormatIeeeFloat = 3;

    static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    static readonly Guid AudioCaptureClientIid = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    IAudioClient? _audioClient;
    IAudioCaptureClient? _captureClient;
    PcmWaveWriter? _writer;
    CancellationTokenSource? _stopSource;
    Task? _captureTask;
    WaveFormat? _format;
    string? _captureError;
    volatile bool _muted;
    volatile bool _paused;
    int _stopped;
    int _disposed;

    public static bool IsAvailable
    {
        get
        {
            try
            {
                using var device = DefaultRenderDevice.Open();
                return device is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    public Task<string?> StartAsync(string outputPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var device = DefaultRenderDevice.Open()
                ?? throw new InvalidOperationException("找不到默认扬声器设备。");
            _audioClient = device.ActivateAudioClient();
            var mixFormat = GetMixFormat(_audioClient);
            try
            {
                _format = WaveFormat.FromNative(mixFormat);
                _writer = new PcmWaveWriter(outputPath, _format);
                ThrowIfFailed(_audioClient.Initialize(
                    ShareModeShared,
                    StreamFlagLoopback,
                    100_000,
                    0,
                    mixFormat,
                    IntPtr.Zero));
            }
            finally
            {
                Marshal.FreeCoTaskMem(mixFormat);
            }

            _captureClient = GetCaptureClient(_audioClient);
            _stopSource = new CancellationTokenSource();
            ThrowIfFailed(_audioClient.Start());
            _captureTask = Task.Run(() => CaptureLoopAsync(_stopSource.Token));
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex)
        {
            Dispose();
            return Task.FromResult<string?>("系统声音录制启动失败：" + ex.Message);
        }
    }

    public void SetMuted(bool muted) => _muted = muted;

    public void SetPaused(bool paused) => _paused = paused;

    public async Task<string?> StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return _captureError;

        try
        {
            _stopSource?.Cancel();
            if (_captureTask is not null)
                await _captureTask.ConfigureAwait(false);
            if (_audioClient is not null)
                ThrowIfFailed(_audioClient.Stop());
            _writer?.Complete();
            return _captureError is null ? null : "系统声音录制失败：" + _captureError;
        }
        catch (Exception ex)
        {
            return "系统声音录制停止失败：" + ex.Message;
        }
        finally
        {
            DisposeCore();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stopSource?.Cancel();
        try { _captureTask?.GetAwaiter().GetResult(); } catch { }
        try { if (_audioClient is not null) ThrowIfFailed(_audioClient.Stop()); } catch { }
        DisposeCore();
    }

    async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var captureClient = _captureClient
                    ?? throw new InvalidOperationException("Audio capture client was not initialized.");
                ThrowIfFailed(captureClient.GetNextPacketSize(out var packetFrames));
                if (packetFrames == 0)
                {
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                ThrowIfFailed(captureClient.GetBuffer(
                    out var data,
                    out var frames,
                    out var flags,
                    out _,
                    out _));
                try
                {
                    if (!_paused)

                    {
                        if (_muted || (flags & BufferFlagSilent) != 0 || data == IntPtr.Zero)
                            _writer?.WriteSilence(frames);
                        else
                            _writer?.WritePcm16(data, frames, _format!);
                    }
                }
                finally
                {
                    ThrowIfFailed(captureClient.ReleaseBuffer(frames));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _captureError = ex.Message;
        }
    }

    static IntPtr GetMixFormat(IAudioClient audioClient)
    {
        ThrowIfFailed(audioClient.GetMixFormat(out var format));
        return format;
    }

    static IAudioCaptureClient GetCaptureClient(IAudioClient audioClient)
    {
        var iid = AudioCaptureClientIid;
        var service = audioClient.GetService(ref iid);
        var unknown = Marshal.GetIUnknownForObject(service);
        try { return (IAudioCaptureClient)Marshal.GetTypedObjectForIUnknown(unknown, typeof(IAudioCaptureClient)); }
        finally { Marshal.Release(unknown); }
    }

    void DisposeCore()
    {
        _writer?.Dispose();
        _writer = null;
        _stopSource?.Dispose();
        _stopSource = null;
        ReleaseComObject(_captureClient);
        _captureClient = null;
        ReleaseComObject(_audioClient);
        _audioClient = null;
        _format = null;
    }

    static void ReleaseComObject(object? value)
    {
        if (value is null) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0) Marshal.ThrowExceptionForHR(hresult);
    }

    sealed class DefaultRenderDevice : IDisposable
    {
        readonly IMMDeviceEnumerator _enumerator;
        readonly IMMDevice _device;

        DefaultRenderDevice(IMMDeviceEnumerator enumerator, IMMDevice device)
        {
            _enumerator = enumerator;
            _device = device;
        }

        public static DefaultRenderDevice? Open()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                ThrowIfFailed(enumerator.GetDefaultAudioEndpoint(0, AudioRoleMultimedia, out var device));
                return new DefaultRenderDevice(enumerator, device);
            }
            catch
            {
                ReleaseComObject(enumerator);
                throw;
            }
        }

        public IAudioClient ActivateAudioClient()
        {
            var iid = AudioClientIid;
            ThrowIfFailed(_device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var value));
            var unknown = Marshal.GetIUnknownForObject(value);
            try { return (IAudioClient)Marshal.GetTypedObjectForIUnknown(unknown, typeof(IAudioClient)); }
            finally { Marshal.Release(unknown); }
        }

        public void Dispose()
        {
            ReleaseComObject(_device);
            ReleaseComObject(_enumerator);
        }
    }

    sealed class WaveFormat
    {
        public ushort Channels { get; }
        public uint SampleRate { get; }
        public ushort BitsPerSample { get; }
        public ushort BlockAlign { get; }
        public bool IsFloat { get; }

        WaveFormat(ushort channels, uint sampleRate, ushort bitsPerSample, ushort blockAlign, bool isFloat)
        {
            Channels = channels;
            SampleRate = sampleRate;
            BitsPerSample = bitsPerSample;
            BlockAlign = blockAlign;
            IsFloat = isFloat;
        }

        public static WaveFormat FromNative(IntPtr nativeFormat)
        {
            var format = Marshal.PtrToStructure<NativeWaveFormat>(nativeFormat);
            var isFloat = format.FormatTag == FormatIeeeFloat;
            if (format.FormatTag == 0xFFFE && format.ExtraSize >= 22)
            {
                var subFormat = Marshal.ReadInt32(nativeFormat, 24);
                isFloat = subFormat == FormatIeeeFloat;
            }

            if (!isFloat && format.FormatTag != FormatPcm && format.FormatTag != 0xFFFE)
                throw new InvalidOperationException($"不支持的系统音频格式：0x{format.FormatTag:X4}");
            if (format.Channels == 0 || format.SampleRate == 0 || format.BitsPerSample == 0)
                throw new InvalidOperationException("系统音频格式无效。");
            return new WaveFormat(format.Channels, format.SampleRate, format.BitsPerSample, format.BlockAlign, isFloat);
        }
    }

    sealed class PcmWaveWriter : IDisposable
    {
        readonly FileStream _stream;
        readonly WaveFormat _format;
        long _dataBytes;
        int _completed;

        public PcmWaveWriter(string path, WaveFormat sourceFormat)
        {
            _format = sourceFormat;
            _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            var header = new byte[44];
            WriteAscii(header, 0, "RIFF");
            WriteAscii(header, 8, "WAVE");
            WriteAscii(header, 12, "fmt ");
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), (short)sourceFormat.Channels);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), (int)sourceFormat.SampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), (int)(sourceFormat.SampleRate * sourceFormat.Channels * 2));
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), (short)(sourceFormat.Channels * 2));
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
            WriteAscii(header, 36, "data");
            _stream.Write(header);
        }

        public void WriteSilence(uint frames)
        {
            var bytes = checked((int)(frames * _format.Channels * 2));
            if (bytes == 0) return;
            var silence = new byte[Math.Min(bytes, 64 * 1024)];
            while (bytes > 0)
            {
                var count = Math.Min(bytes, silence.Length);
                _stream.Write(silence, 0, count);
                _dataBytes += count;
                bytes -= count;
            }
        }

        public void WritePcm16(IntPtr data, uint frames, WaveFormat sourceFormat)
        {
            var sampleCount = checked((int)(frames * sourceFormat.Channels));
            var output = new byte[checked(sampleCount * 2)];
            var input = new byte[checked((int)(frames * sourceFormat.BlockAlign))];
            Marshal.Copy(data, input, 0, input.Length);
            var inputBytesPerSample = Math.Max(1, sourceFormat.BitsPerSample / 8);
            for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                var inputOffset = sampleIndex * inputBytesPerSample;
                var sample = ReadSample(input, inputOffset, inputBytesPerSample, sourceFormat.IsFloat);
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(sampleIndex * 2), sample);
            }
            _stream.Write(output);
            _dataBytes += output.Length;
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            var end = _stream.Position;
            _stream.Position = 4;
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, checked((int)(end - 8)));
            _stream.Write(size);
            _stream.Position = 40;
            BinaryPrimitives.WriteInt32LittleEndian(size, checked((int)_dataBytes));
            _stream.Write(size);
            _stream.Position = end;
            _stream.Flush(true);
        }

        public void Dispose()
        {
            try { Complete(); } catch { }
            _stream.Dispose();
        }

        static short ReadSample(byte[] input, int offset, int bytesPerSample, bool isFloat)
        {
            if (isFloat && bytesPerSample >= 4)
            {
                var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(offset)));
                return (short)Math.Clamp((int)Math.Round(value * short.MaxValue), short.MinValue, short.MaxValue);
            }

            return bytesPerSample switch
            {
                1 => (short)((input[offset] - 128) << 8),
                2 => BinaryPrimitives.ReadInt16LittleEndian(input.AsSpan(offset)),
                3 => (short)(ReadInt24(input, offset) >> 8),
                _ => (short)(BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(offset)) >> 16),
            };
        }

        static int ReadInt24(byte[] input, int offset)
        {
            var value = input[offset] | (input[offset + 1] << 8) | (input[offset + 2] << 16);
            return (value & 0x0080_0000) != 0 ? value | unchecked((int)0xFF00_0000) : value;
        }

        static void WriteAscii(byte[] bytes, int offset, string value)
            => Encoding.ASCII.GetBytes(value, bytes.AsSpan(offset, value.Length));
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct NativeWaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SampleRate;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out object devices);
        int GetDefaultAudioEndpoint(int dataFlow, uint role, out IMMDevice endpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), ClassInterface(ClassInterfaceType.None)]
    class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, uint clsContext, IntPtr activationParams, [MarshalAs(UnmanagedType.Interface)] out object interfacePointer);
        int OpenPropertyStore(int access, out object properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out uint state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        int GetBufferSize(out uint bufferSize);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint padding);
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr eventHandle);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetService(ref Guid serviceIid);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out uint numFrames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        int ReleaseBuffer(uint numFramesRead);
        int GetNextPacketSize(out uint numFramesInNextPacket);
    }
}