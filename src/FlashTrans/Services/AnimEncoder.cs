using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlashTrans.Core;

namespace FlashTrans.Services;

/// <summary>编出来的动图：存到哪儿、什么格式、多大。</summary>
public sealed record AnimResult(string Path, RecordFormat Format, long Bytes)
{
    /// <summary>用户本来想要的格式。跟 Format 不一样就是退过档了。</summary>
    public RecordFormat Wanted { get; init; }

    /// <summary>退档的原因，直接拿去给用户看。没退档就是空的。</summary>
    public string? FellBackWhy { get; init; }

    public bool FellBack => Wanted != Format;
}

/// <summary>
/// 把一串临时位图帧编成动图。
///
/// GIF 这条自己就能编完：LZW 和调色板借 WPF 的 GifBitmapEncoder，它缺的只是
/// 帧延时和循环标记，编完在字节流里补上（见 PatchGif）。
///
/// WebP 这条必须靠外部的 img2webp.exe。WPF 里一个 WebP 编码器都没有，
/// 而且就算系统装了 WebP 解码器（Win10 1809+ 有），那也只解不编；
/// 动图 WebP 还要额外写 VP8X/ANIM/ANMF 这几个容器块，WIC 的编码器接口
/// 压根没有对应的写法。所以找不到那个程序时只能退回 GIF。
/// </summary>
public static class AnimEncoder
{
    /// <summary>img2webp.exe 相对 exe 目录的位置。</summary>
    public const string Img2WebpRelative = @"Assets\tools\img2webp.exe";

    /// <summary>找 img2webp。没有就返回 null，调用方据此退回 GIF。</summary>
    public static string? FindImg2Webp()
    {
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, Img2WebpRelative);
            return File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    public static bool WebpAvailable => FindImg2Webp() is not null;

    /// <summary>GIF 的延时单位是 1/100 秒，WebP 的是毫秒。</summary>
    internal static int DelayCentis(int fps)
        => Math.Max(2, (int)Math.Round(100.0 / Math.Max(1, fps)));

    /// <summary>
    /// GIF 只能用厘秒记录延时。用累计时间取整，让 60 fps 在 1/2 厘秒之间交错，
    /// 而不是把每一帧都粗暴地固定成 2 厘秒。
    /// </summary>
    internal static int GifDelayCentis(int fps, int frameIndex)
    {
        fps = Math.Max(1, fps);
        frameIndex = Math.Max(0, frameIndex);
        var start = Math.Round(frameIndex * 100.0 / fps, MidpointRounding.AwayFromZero);
        var end = Math.Round((frameIndex + 1) * 100.0 / fps, MidpointRounding.AwayFromZero);
        return Math.Max(1, (int)(end - start));
    }

    internal static int DelayMillis(int fps)
        => Math.Max(10, (int)Math.Round(1000.0 / Math.Max(1, fps)));

    /// <summary>
    /// 把 frames（按顺序的临时位图路径）编成一张动图，存到 outPath 去掉扩展名之后
    /// 加上真正的后缀。返回实际存成了什么。
    /// audioPath: 可选的音频文件路径（只对 MP4 有效）。
    /// </summary>
    public static async Task<AnimResult> SaveAsync(
        IReadOnlyList<string> frames, string outNoExt, int fps, RecordFormat want,
        string? audioPath = null)
    {
        if (frames.Count == 0) throw new InvalidOperationException("没有帧可以编码。");

        var dir = Path.GetDirectoryName(outNoExt);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // MP4 走系统自带的 H.264 编码器。编码失败时不能静默改成另一种格式，
        // 否则用户会同时看到一个空 MP4 和一个自己没选的 WebP。
        if (want == RecordFormat.Mp4)
        {
            var mp4Path = outNoExt + ".mp4";
            try
            {
                var m = await Mp4Encoder.SaveAsync(frames, mp4Path, fps, audioPath);
                return m with { Wanted = want };
            }
            catch (Exception ex)
            {
                Log.Error("编 MP4 失败，未生成替代文件", ex);
                throw new InvalidOperationException("MP4 编码失败：" + ex.Message, ex);
            }
        }

        if (want == RecordFormat.Webp) return await SaveWebpOrGifAsync(frames, outNoExt, fps);
        return (await SaveGifAsync(frames, outNoExt + ".gif", fps)) with { Wanted = want };
    }

    /// <summary>WebP，没有 img2webp 就 GIF。</summary>
    static async Task<AnimResult> SaveWebpOrGifAsync(
        IReadOnlyList<string> frames, string outNoExt, int fps)
    {
        var tool = FindImg2Webp();
        if (tool is null)
        {
            // 不是错误：绿色版被人只拷了个 exe 走也会走到这儿。
            Log.Warn($"没找到 {Img2WebpRelative}，这次录制改存 GIF。");
            var g = await SaveGifAsync(frames, outNoExt + ".gif", fps);
            return g with { Wanted = RecordFormat.Webp, FellBackWhy = "没找到 img2webp" };
        }
        var w = await SaveWebpAsync(tool, frames, outNoExt + ".webp", fps);
        return w with { Wanted = RecordFormat.Webp };
    }

    /// <summary>
    /// 调 img2webp 编动图 WebP。
    ///
    /// 帧名只传文件名，工作目录设成帧所在的临时目录：300 帧的绝对路径拼起来
    /// 能有两三万字符，逼近命令行长度上限，超了之后的表现是「参数被截断」，
    /// 排查起来很难看出原因。
    /// 参数用 ArgumentList 一项一项给，转义交给运行库——路径里有空格是常态。
    /// </summary>
    static async Task<AnimResult> SaveWebpAsync(
        string tool, IReadOnlyList<string> frames, string outPath, int fps)
    {
        var work = Path.GetDirectoryName(frames[0]) ?? AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = tool,
            WorkingDirectory = work,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-loop");
        psi.ArgumentList.Add("0");            // 0 = 一直循环
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(DelayMillis(fps).ToString());
        psi.ArgumentList.Add("-lossy");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("80");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outPath);
        foreach (var f in frames) psi.ArgumentList.Add(Path.GetFileName(f));

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("启动 img2webp 失败。");

        // 两个流都要读。只读一个的话另一个的缓冲写满就卡住不动了。
        var errTask = proc.StandardError.ReadToEndAsync();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        using var kill = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await proc.WaitForExitAsync(kill.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("img2webp 跑太久了，已经中断。");
        }

        var err = (await errTask).Trim();
        _ = await outTask;
        if (proc.ExitCode != 0 || !File.Exists(outPath))
            throw new InvalidOperationException(
                $"img2webp 失败（退出码 {proc.ExitCode}）{(err.Length > 0 ? "：" + err : "")}");

        return new AnimResult(outPath, RecordFormat.Webp, new FileInfo(outPath).Length);
    }

    static async Task<AnimResult> SaveGifAsync(
        IReadOnlyList<string> frames, string outPath, int fps)
    {
        await Task.Run(() => BuildGif(frames, outPath, fps));
        return new AnimResult(outPath, RecordFormat.Gif, new FileInfo(outPath).Length);
    }

    /// <summary>
    /// 拼一张动图 GIF。
    ///
    /// 为什么不把所有帧交给一个 GifBitmapEncoder：它要等 Save 时才写，
    /// 在那之前全部帧都得在内存里。录一块 2560×1440 的区域、300 帧，
    /// 光源位图就是 4GB 出头，直接 OOM。
    /// 这里改成每帧单独编成一张单帧 GIF，把它的调色板和 LZW 码流抠出来接到
    /// 输出流上，内存里同时只有一帧。
    /// 附带的好处是每帧带自己的局部调色板（WIC 按这一帧的实际颜色挑），
    /// 比所有帧共用一个全局调色板少很多色带。
    /// </summary>
    internal static void BuildGif(IReadOnlyList<string> frames, string outPath, int fps)
    {
        using var fs = File.Create(outPath);
        var started = false;
        var frameIndex = 0;

        foreach (var path in frames)
        {
            var one = SplitSingleGif(EncodeSingleGif(path));
            if (one is null) continue;

            if (!started)
            {
                WriteGifHeader(fs, one.Width, one.Height);
                started = true;
            }
            WriteGifFrame(fs, one, GifDelayCentis(fps, frameIndex++));
        }

        if (!started) throw new InvalidOperationException("一帧都没能编码。");
        fs.WriteByte(0x3B);   // Trailer
    }

    /// <summary>从一张单帧 GIF 里抠出来的东西：调色板加 LZW 码流，能直接接到动图里去。</summary>
    internal sealed record GifFrameParts(
        int Width, int Height, byte[] Palette, int PaletteBits, bool Interlaced, byte[] Lzw);

    /// <summary>
    /// 把一张 PNG 编成单帧 GIF。量化和 LZW 都是 WIC 干的——
    /// 自己写 LZW 没有意义，而颜色量化写不好就是一脸色带。
    /// </summary>
    static byte[] EncodeSingleGif(string pngPath)
    {
        var enc = new GifBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(LoadFrame(pngPath)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 读一帧。OnLoad 是必须的：默认的延迟加载会把流一直握着，
    /// 我们这儿读完就要删临时文件。
    /// </summary>
    internal static BitmapSource LoadFrame(string path)
    {
        using var fs = File.OpenRead(path);
        var f = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        f.Freeze();
        return f;
    }

    /// <summary>
    /// 拆一张单帧 GIF：走到图像描述符，把调色板和 LZW 码流原样抠出来。
    /// 码流不用重新压——调色板索引跟调色板放在全局还是局部无关，
    /// 所以后面可以把它当局部调色板接到动图里去。
    /// </summary>
    internal static GifFrameParts? SplitSingleGif(byte[] g)
    {
        if (g.Length < 13 || g[0] != 'G' || g[1] != 'I' || g[2] != 'F') return null;

        var p = 10;
        var packed = g[p];
        p = 13;
        byte[] global = [];
        var globalBits = (packed & 0x07) + 1;
        if ((packed & 0x80) != 0)
        {
            var n = 3 * (1 << globalBits);
            if (p + n > g.Length) return null;
            global = g[p..(p + n)];
            p += n;
        }

        while (p < g.Length)
        {
            var b = g[p];
            if (b == 0x3B) return null;                    // 走到结尾也没有图像块
            if (b == 0x21)                                  // 扩展块，跳过
            {
                p += 2;                                     // introducer + label
                p = SkipSubBlocks(g, p);
                continue;
            }
            if (b != 0x2C) return null;                     // 不认识的块，别猜

            if (p + 10 > g.Length) return null;
            var w = g[p + 5] | (g[p + 6] << 8);
            var h = g[p + 7] | (g[p + 8] << 8);
            var ip = g[p + 9];
            p += 10;

            var pal = global;
            var bits = globalBits;
            if ((ip & 0x80) != 0)                           // 有局部调色板，优先用
            {
                bits = (ip & 0x07) + 1;
                var n = 3 * (1 << bits);
                if (p + n > g.Length) return null;
                pal = g[p..(p + n)];
                p += n;
            }
            if (pal.Length == 0) return null;

            var start = p;
            p++;                                            // LZW 最小码长
            p = SkipSubBlocks(g, p);
            return new GifFrameParts(w, h, pal, bits, (ip & 0x40) != 0, g[start..p]);
        }
        return null;
    }

    /// <summary>跳过一串数据子块，返回结尾那个 0 之后的位置。</summary>
    static int SkipSubBlocks(byte[] g, int p)
    {
        while (p < g.Length)
        {
            var len = g[p];
            p++;
            if (len == 0) break;
            p += len;
        }
        return Math.Min(p, g.Length);
    }

    /// <summary>
    /// 画布头。不写全局调色板：每帧自带局部的，写了也用不上。
    /// 后面紧跟 NETSCAPE2.0 那个扩展块——没有它，GIF 只播一遍就停在最后一帧，
    /// 这是「录出来的动图不动」最常见的原因。
    /// </summary>
    static void WriteGifHeader(Stream s, int w, int h)
    {
        s.Write("GIF89a"u8);
        Le16(s, w);
        Le16(s, h);
        s.WriteByte(0x00);   // 无全局调色板
        s.WriteByte(0x00);   // 背景色索引
        s.WriteByte(0x00);   // 像素宽高比

        s.Write([0x21, 0xFF, 0x0B]);
        s.Write("NETSCAPE2.0"u8);
        s.Write([0x03, 0x01]);
        Le16(s, 0);          // 0 = 无限循环
        s.WriteByte(0x00);
    }

    static void WriteGifFrame(Stream s, GifFrameParts f, int delayCentis)
    {
        // 图形控制扩展：延时在这儿。处置方式给 1（不处置）——每帧都是整块不透明的
        // 画面，直接盖上去就行；给 2（恢复背景色）会在帧之间闪一下背景。
        s.Write([0x21, 0xF9, 0x04, 0x04]);
        Le16(s, delayCentis);
        s.WriteByte(0x00);   // 透明色索引（上面没开透明标志，写什么都不生效）
        s.WriteByte(0x00);

        s.WriteByte(0x2C);
        Le16(s, 0);          // 左
        Le16(s, 0);          // 上
        Le16(s, f.Width);
        Le16(s, f.Height);
        s.WriteByte((byte)(0x80 | (f.Interlaced ? 0x40 : 0x00) | ((f.PaletteBits - 1) & 0x07)));
        s.Write(f.Palette);
        s.Write(f.Lzw);
    }

    static void Le16(Stream s, int v)
    {
        s.WriteByte((byte)(v & 0xFF));
        s.WriteByte((byte)((v >> 8) & 0xFF));
    }
}
