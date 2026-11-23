using System.Runtime.InteropServices;
using System.Windows;

namespace FlashTrans.Interop;

internal sealed record NativeLanguage(string LanguageTag, string DisplayName, string Script)
{
    internal static unsafe NativeCom Create(string tag)
    {
        using var factory = NativeWinRt.Factory("Windows.Globalization.Language", new Guid("9b0252ac-0c27-44f8-b792-9793fb66c63e"));
        using var languageTag = new NativeWinRt.HString(tag);
        nint language = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)NativeWinRt.Slot(factory.Pointer, 6))(factory.Pointer, languageTag.Pointer, &language));
        return new NativeCom(language);
    }

    internal static NativeLanguage FromTag(string tag)
    {
        using var apartment = new NativeWinRt.Apartment();
        using var language = Create(tag);
        return Read(language);
    }

    internal static NativeLanguage Read(NativeCom language) => new(language.GetString(6), language.GetString(7), language.GetString(9));
}

internal sealed record NativeOcrWord(string Text, Rect BoundingRect);
internal sealed record NativeOcrLine(string Text, IReadOnlyList<NativeOcrWord> Words);
internal sealed record NativeOcrResult(IReadOnlyList<NativeOcrLine> Lines, double? TextAngle);

internal sealed unsafe class NativeOcrEngine : IDisposable
{
    readonly NativeCom _engine;
    NativeOcrEngine(NativeCom engine) => _engine = engine;

    static NativeCom Statics() => NativeWinRt.Factory("Windows.Media.Ocr.OcrEngine", new Guid("5bffa85a-3384-3540-9940-699120d428a8"));

    internal static uint MaxImageDimension
    {
        get { using var apartment = new NativeWinRt.Apartment(); using var factory = Statics(); return factory.GetUInt32(6); }
    }

    internal static IReadOnlyList<NativeLanguage> AvailableRecognizerLanguages
    {
        get
        {
            using var apartment = new NativeWinRt.Apartment();
            using var factory = Statics();
            using var vector = factory.GetObject(7);
            var result = new List<NativeLanguage>();
            var count = vector.GetUInt32(7);
            for (uint i = 0; i < count; i++)
            {
                using var language = vector.GetAt(i);
                result.Add(NativeLanguage.Read(language));
            }
            return result;
        }
    }

    internal static NativeOcrEngine? TryCreateFromLanguage(string tag)
    {
        using var apartment = new NativeWinRt.Apartment();
        using var factory = Statics();
        using var language = NativeLanguage.Create(tag);
        nint engine = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)NativeWinRt.Slot(factory.Pointer, 9))(factory.Pointer, language.Pointer, &engine));
        return engine == 0 ? null : new NativeOcrEngine(new NativeCom(engine));
    }

    internal Task<NativeOcrResult> RecognizeAsync(CapturedImage image, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        using var apartment = new NativeWinRt.Apartment();
        using var bitmap = CreateBitmap(image);
        nint pending = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)NativeWinRt.Slot(_engine.Pointer, 6))(_engine.Pointer, bitmap.Pointer, &pending));
        using var operation = new NativeCom(pending);
        using var result = NativeAsync.Wait(operation, typeof(IOcrCompletedHandler), ct);
        using var linesVector = result.GetObject(6);
        var lines = new List<NativeOcrLine>();
        var count = linesVector.GetUInt32(7);
        for (uint i = 0; i < count; i++)
        {
            using var line = linesVector.GetAt(i);
            using var wordsVector = line.GetObject(6);
            var words = new List<NativeOcrWord>();
            var wordCount = wordsVector.GetUInt32(7);
            for (uint j = 0; j < wordCount; j++)
            {
                using var word = wordsVector.GetAt(j);
                NativeRect rect = default;
                NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, NativeRect*, int>)NativeWinRt.Slot(word.Pointer, 6))(word.Pointer, &rect));
                words.Add(new NativeOcrWord(word.GetString(7), new Rect(rect.X, rect.Y, rect.Width, rect.Height)));
            }
            lines.Add(new NativeOcrLine(line.GetString(7), words));
        }
        nint anglePointer = 0;
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)NativeWinRt.Slot(result.Pointer, 7))(result.Pointer, &anglePointer));
        double? angle = null;
        if (anglePointer != 0)
        {
            using var reference = new NativeCom(anglePointer);
            double value = 0;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, double*, int>)NativeWinRt.Slot(reference.Pointer, 6))(reference.Pointer, &value));
            angle = value;
        }
        ct.ThrowIfCancellationRequested();
        return new NativeOcrResult(lines, angle);
    }, ct);

    static NativeCom CreateBitmap(CapturedImage image)
    {
        using var factory = NativeWinRt.Factory("Windows.Graphics.Imaging.SoftwareBitmap", new Guid("c99feb69-2d62-4d47-a6b3-4fdb6a07fdf8"));
        nint pointer = 0;
        // ISoftwareBitmapFactory.CreateWithAlpha：Bgra8=87，Premultiplied=0。
        NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, int, int, int, int, nint*, int>)NativeWinRt.Slot(factory.Pointer, 7))(factory.Pointer, 87, image.Width, image.Height, 0, &pointer));
        var bitmap = new NativeCom(pointer, close: true);
        try
        {
            nint lockedPointer = 0;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)NativeWinRt.Slot(bitmap.Pointer, 15))(bitmap.Pointer, 2, &lockedPointer));
            using var locked = new NativeCom(lockedPointer, close: true);
            BitmapPlane plane = default;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, int, BitmapPlane*, int>)NativeWinRt.Slot(locked.Pointer, 7))(locked.Pointer, 0, &plane));
            using var memoryBuffer = locked.Query(new Guid("fbc4dd2a-245b-11e4-af98-689423260cf8"));
            using var reference = memoryBuffer.GetObject(6, close: true);
            using var access = reference.Query(new Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d"));
            nint pixels = 0;
            uint capacity = 0;
            NativeWinRt.Check(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, int>)NativeWinRt.Slot(access.Pointer, 3))(access.Pointer, &pixels, &capacity));
            // 平面布局来自原生 API，必须按容量和实际 stride 校验后才写入。
            var firstRow = (long)plane.StartIndex;
            var lastRow = firstRow + (long)(image.Height - 1) * plane.Stride;
            if (plane.Width != image.Width || plane.Height != image.Height || pixels == 0
                || Math.Min(firstRow, lastRow) < 0 || Math.Max(firstRow, lastRow) + image.Stride > capacity)
                throw new InvalidOperationException("系统 OCR 位图缓冲区布局无效。");
            for (var y = 0; y < image.Height; y++)
                Marshal.Copy(image.Pixels, y * image.Stride, pixels + checked(plane.StartIndex + y * plane.Stride), image.Stride);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public float X, Y, Width, Height; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapPlane { public int StartIndex, Width, Height, Stride; }
    public void Dispose() => _engine.Dispose();
}
