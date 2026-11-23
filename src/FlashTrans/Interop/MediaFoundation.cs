using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlashTrans.Interop;

/// <summary>Media Foundation 的编码/解码 ABI；不需要 Windows SDK 托管投影。</summary>
internal static unsafe class MediaFoundation
{
    internal static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");
    internal static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    internal static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    internal static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    internal static readonly Guid Aac = new("00001610-0000-0010-8000-00aa00389b71");
    internal static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    internal static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    internal static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    internal static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    internal static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    internal static readonly Guid SampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    internal static readonly Guid AudioBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    internal static readonly Guid BlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    internal static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

    internal sealed class Lifetime : IDisposable
    {
        internal Lifetime() => NativeWinRt.Check(MFStartup(0x20070, 0));
        public void Dispose() => NativeWinRt.Check(MFShutdown());
    }

    internal static NativeCom MediaType()
    {
        NativeWinRt.Check(MFCreateMediaType(out var type));
        return new NativeCom(type);
    }

    internal static NativeCom Attributes()
    {
        NativeWinRt.Check(MFCreateAttributes(out var attributes, 4));
        return new NativeCom(attributes);
    }

    // IMFAttributes 在 IUnknown 后有 30 个方法；派生的 IMFMediaType / IMFSample 沿用前缀。
    internal static void Set(NativeCom attributes, Guid key, uint value) =>
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)NativeWinRt.Slot(attributes.Pointer, 21))(attributes.Pointer, &key, value));
    internal static void Set(NativeCom attributes, Guid key, ulong value) =>
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)NativeWinRt.Slot(attributes.Pointer, 22))(attributes.Pointer, &key, value));
    internal static void Set(NativeCom attributes, Guid key, Guid value) =>
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)NativeWinRt.Slot(attributes.Pointer, 24))(attributes.Pointer, &key, &value));
    internal static ulong Pair(uint numerator, uint denominator) => ((ulong)numerator << 32) | denominator;

    internal static NativeCom SinkWriter(string path)
    {
        using var attributes = Attributes();
        Set(attributes, new Guid("a634a91c-822b-41b9-a494-4de4643612b0"), 1u);
        Set(attributes, new Guid("08b845d8-2b74-4afe-9d53-be16d2d5ae4f"), 1u);
        NativeWinRt.Check(MFCreateSinkWriterFromURL(path, 0, attributes.Pointer, out var writer));
        return new NativeCom(writer);
    }

    internal static uint AddStream(NativeCom writer, NativeCom output)
    {
        uint index = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)NativeWinRt.Slot(writer.Pointer, 3))(writer.Pointer, output.Pointer, &index));
        return index;
    }

    internal static void SetInput(NativeCom writer, uint index, NativeCom input) =>
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)NativeWinRt.Slot(writer.Pointer, 4))(writer.Pointer, index, input.Pointer, 0));
    internal static void Begin(NativeCom writer) =>
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, int>)NativeWinRt.Slot(writer.Pointer, 5))(writer.Pointer));
    internal static void Finish(NativeCom writer) =>
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, int>)NativeWinRt.Slot(writer.Pointer, 11))(writer.Pointer));

    internal static void Write(NativeCom writer, uint stream, byte[] bytes, int length, long time, long duration)
    {
        NativeWinRt.Check(MFCreateMemoryBuffer(checked((uint)length), out var bufferPointer));
        using var buffer = new NativeCom(bufferPointer);
        nint data = 0;
        uint maximum = 0;
        uint current = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, int>)NativeWinRt.Slot(buffer.Pointer, 3))(buffer.Pointer, &data, &maximum, &current));
        try
        {
            if (maximum < length || data == 0) throw new InvalidOperationException("系统编码缓冲区容量不足。");
            Marshal.Copy(bytes, 0, data, length);
        }
        finally { NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, int>)NativeWinRt.Slot(buffer.Pointer, 4))(buffer.Pointer)); }
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)NativeWinRt.Slot(buffer.Pointer, 6))(buffer.Pointer, (uint)length));
        NativeWinRt.Check(MFCreateSample(out var samplePointer));
        using var sample = new NativeCom(samplePointer);
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)NativeWinRt.Slot(sample.Pointer, 42))(sample.Pointer, buffer.Pointer));
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, long, int>)NativeWinRt.Slot(sample.Pointer, 36))(sample.Pointer, time));
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, long, int>)NativeWinRt.Slot(sample.Pointer, 38))(sample.Pointer, duration));
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)NativeWinRt.Slot(writer.Pointer, 6))(writer.Pointer, stream, sample.Pointer));
    }

    internal static BitmapSource FirstVideoFrame(string path)
    {
        using var apartment = new NativeWinRt.Apartment();
        using var foundation = new Lifetime();
        using var attributes = Attributes();
        Set(attributes, new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d"), 1u);
        NativeWinRt.Check(MFCreateSourceReaderFromURL(Path.GetFullPath(path), attributes.Pointer, out var readerPointer));
        using var reader = new NativeCom(readerPointer);
        const uint videoStream = 0xfffffffc;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)NativeWinRt.Slot(reader.Pointer, 4))(reader.Pointer, 0xfffffffe, 0));
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)NativeWinRt.Slot(reader.Pointer, 4))(reader.Pointer, videoStream, 1));
        using var desired = MediaType();
        Set(desired, MajorType, Video);
        Set(desired, Subtype, Rgb32);
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)NativeWinRt.Slot(reader.Pointer, 7))(reader.Pointer, videoStream, 0, desired.Pointer));
        while (true)
        {
            uint stream = 0;
            uint flags = 0;
            long time = 0;
            nint samplePointer = 0;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)NativeWinRt.Slot(reader.Pointer, 9))(reader.Pointer, videoStream, 0, &stream, &flags, &time, &samplePointer));
            if (samplePointer == 0)
            {
                if ((flags & 2) != 0) throw new InvalidDataException("MP4 中没有可解码的视频帧。");
                continue;
            }
            using var sample = new NativeCom(samplePointer);
            nint typePointer = 0;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)NativeWinRt.Slot(reader.Pointer, 6))(reader.Pointer, videoStream, &typePointer));
            using var type = new NativeCom(typePointer);
            var key = FrameSize;
            ulong size = 0;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)NativeWinRt.Slot(type.Pointer, 8))(type.Pointer, &key, &size));
            var width = checked((int)(size >> 32));
            var height = checked((int)(size & uint.MaxValue));
            using var buffer = sample.GetObject(41);
            var pixels = CopyRgb32(buffer, width, height);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, checked(width * 4));
            bitmap.Freeze();
            return bitmap;
        }
    }

    static byte[] CopyRgb32(NativeCom buffer, int width, int height)
    {
        var rowBytes = checked(width * 4);
        var pixels = new byte[checked(rowBytes * height)];
        var iid = new Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
        nint buffer2DPointer = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)NativeWinRt.Slot(buffer.Pointer, 0))(buffer.Pointer, &iid, &buffer2DPointer);
        if (hr >= 0)
        {
            using var buffer2D = new NativeCom(buffer2DPointer);
            nint scanline = 0;
            int stride = 0;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int*, int>)NativeWinRt.Slot(buffer2D.Pointer, 3))(buffer2D.Pointer, &scanline, &stride));
            try
            {
                if (scanline == 0 || Math.Abs((long)stride) < rowBytes) throw new InvalidDataException("解码帧行宽无效。");
                // Lock2D 的地址指向画面顶行，stride 可正可负，不能再无条件 FlipRows。
                for (var y = 0; y < height; y++) Marshal.Copy(scanline + checked(y * stride), pixels, y * rowBytes, rowBytes);
            }
            finally { NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, int>)NativeWinRt.Slot(buffer2D.Pointer, 4))(buffer2D.Pointer)); }
        }
        else
        {
            if (hr != unchecked((int)0x80004002)) NativeWinRt.Check(hr);
            nint data = 0;
            uint maximum = 0;
            uint current = 0;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, int>)NativeWinRt.Slot(buffer.Pointer, 3))(buffer.Pointer, &data, &maximum, &current));
            try
            {
                if (data == 0 || current < pixels.Length) throw new InvalidDataException("解码帧像素不足。");
                // 非 2D 的 RGB32 连续布局遵循 DIB 的 bottom-up 约定。
                for (var y = 0; y < height; y++) Marshal.Copy(data + (height - 1 - y) * rowBytes, pixels, y * rowBytes, rowBytes);
            }
            finally { NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, int>)NativeWinRt.Slot(buffer.Pointer, 4))(buffer.Pointer)); }
        }
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return pixels;
    }

    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFShutdown();
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out nint type);
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out nint attributes, uint initialSize);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out nint sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(uint length, out nint buffer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSinkWriterFromURL(string path, nint stream, nint attributes, out nint writer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSourceReaderFromURL(string path, nint attributes, out nint reader);
}
