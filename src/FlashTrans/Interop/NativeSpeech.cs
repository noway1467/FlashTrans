using System.IO;
using System.Runtime.InteropServices;

namespace FlashTrans.Interop;

internal sealed record NativeVoice(string Id, string Language);

internal static unsafe class NativeSpeech
{
    static NativeCom Statics() => NativeWinRt.Factory("Windows.Media.SpeechSynthesis.SpeechSynthesizer", new Guid("7d526ecc-7533-4c3f-85be-888c2baeeBDC"));

    internal static IReadOnlyList<NativeVoice> AllVoices
    {
        get
        {
            using var apartment = new NativeWinRt.Apartment();
            using var factory = Statics();
            using var vector = factory.GetObject(6);
            var result = new List<NativeVoice>();
            var count = vector.GetUInt32(7);
            for (uint i = 0; i < count; i++)
            {
                using var voice = vector.GetAt(i);
                result.Add(new NativeVoice(voice.GetString(7), voice.GetString(8)));
            }
            return result;
        }
    }

    internal static NativeVoice DefaultVoice
    {
        get
        {
            using var apartment = new NativeWinRt.Apartment();
            using var factory = Statics();
            using var voice = factory.GetObject(7);
            return new NativeVoice(voice.GetString(7), voice.GetString(8));
        }
    }

    internal static Task<byte[]> SynthesizeAsync(string text, NativeVoice chosen, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var apartment = new NativeWinRt.Apartment();
        using var synth = NativeWinRt.Activate("Windows.Media.SpeechSynthesis.SpeechSynthesizer", new Guid("ce9f7c76-97f4-4ced-ad68-d51c458e45c6"));
        using var factory = Statics();
        using var voices = factory.GetObject(6);
        var found = false;
        var count = voices.GetUInt32(7);
        for (uint i = 0; i < count; i++)
        {
            using var voice = voices.GetAt(i);
            if (voice.GetString(7) != chosen.Id) continue;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)NativeWinRt.Slot(synth.Pointer, 8))(synth.Pointer, voice.Pointer));
            found = true;
            break;
        }
        if (!found) throw new InvalidOperationException("选中的系统语音已不可用。");
        using var content = new NativeWinRt.HString(text);
        nint pending = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)NativeWinRt.Slot(synth.Pointer, 6))(synth.Pointer, content.Pointer, &pending));
        using var operation = new NativeCom(pending);
        using var result = NativeAsync.Wait(operation, typeof(ISpeechCompletedHandler), ct);
        using var stream = result.Query(new Guid("905a0fe1-bc53-11df-8c49-001e4fc686da"), close: true);
        ulong size = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, ulong*, int>)NativeWinRt.Slot(stream.Pointer, 6))(stream.Pointer, &size));
        if (size > int.MaxValue) throw new InvalidDataException("系统语音流过大。");
        nint inputPointer = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, ulong, nint*, int>)NativeWinRt.Slot(stream.Pointer, 8))(stream.Pointer, 0, &inputPointer));
        using var input = new NativeCom(inputPointer, close: true);
        using var bufferFactory = NativeWinRt.Factory("Windows.Storage.Streams.Buffer", new Guid("71af914d-c10f-484b-bc50-14bc623b3a27"));
        const uint chunkSize = 64 * 1024;
        nint bufferPointer = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)NativeWinRt.Slot(bufferFactory.Pointer, 6))(bufferFactory.Pointer, chunkSize, &bufferPointer));
        using var buffer = new NativeCom(bufferPointer);
        using var output = new MemoryStream(checked((int)size));
        var bytes = new byte[chunkSize];
        while ((ulong)output.Length < size)
        {
            ct.ThrowIfCancellationRequested();
            var requested = (uint)Math.Min(chunkSize, size - (ulong)output.Length);
            nint reading = 0;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, int, nint*, int>)NativeWinRt.Slot(input.Pointer, 6))(input.Pointer, buffer.Pointer, requested, 0, &reading));
            using var readOperation = new NativeCom(reading);
            using var readBuffer = NativeAsync.Wait(readOperation, typeof(IBufferCompletedHandler), ct, withProgress: true);
            var length = readBuffer.GetUInt32(7);
            if (length == 0 || length > requested) throw new EndOfStreamException("系统语音流长度不匹配。");
            using var access = readBuffer.Query(new Guid("905a0fef-bc53-11df-8c49-001e4fc686da")); // IBufferByteAccess
            nint data = 0;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)NativeWinRt.Slot(access.Pointer, 3))(access.Pointer, &data));
            Marshal.Copy(data, bytes, 0, (int)length);
            output.Write(bytes, 0, (int)length);
        }
        ct.ThrowIfCancellationRequested();
        return output.ToArray();
    }, ct);
}
