using System.IO;
using System.Runtime.InteropServices;
using FlashTrans.Interop;
using RapidOcrNet;
using SkiaSharp;

namespace FlashTrans.Services;

public static partial class OcrService
{
    // ---- RapidOCR（本地 ONNX 模型）----
    // 模型文件放在 exe 同目录 models\v6\ 下，随发布包分发（Apache-2.0）。
    // PP-OCRv6 tiny：检测 1.7MB + 方向 1MB + 识别 4.3MB + 词表。实测一张
    // 1449x678 截图约 1.8s（首次含加载），中英混排质量明显好于系统 OCR。
    const string RapidModelsRoot = "models";
    const string RapidModelsVersion = "v6";

    static readonly string[] RapidModelFiles =
    [
        "PP-OCRv6_det_tiny.onnx",
        "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
        "PP-OCRv6_rec_tiny.onnx",
        "ppocrv6_tiny_dict.txt",
    ];

    static readonly object RapidGate = new();
    static RapidOcr? _rapid;
    static bool _rapidTried;

    /// <summary>RapidOCR 模型文件是否齐全（只查文件，不触发加载）。</summary>
    public static bool RapidModelsPresent
    {
        get
        {
            var dir = RapidModelsDir;
            return dir is not null && RapidModelFiles.All(f => File.Exists(Path.Combine(dir, f)));
        }
    }

    /// <summary>缺了哪些模型文件，给设置页和报错文案用。</summary>
    public static string? RapidModelsHint()
    {
        var dir = RapidModelsDir;
        if (dir is null)
            return $"找不到模型目录：{Path.Combine(AppContext.BaseDirectory, RapidModelsRoot, RapidModelsVersion)}";
        var missing = RapidModelFiles.Where(f => !File.Exists(Path.Combine(dir, f))).ToArray();
        return missing.Length == 0 ? null : $"模型目录缺文件：{string.Join("、", missing)}";
    }

    static string? RapidModelsDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, RapidModelsRoot, RapidModelsVersion);
            return Directory.Exists(dir) ? dir : null;
        }
    }

    /// <summary>懒加载 RapidOCR。失败记日志返回 null，让调用方回退系统 OCR。</summary>
    static RapidOcr? RapidEngine()
    {
        lock (RapidGate)
        {
            if (_rapidTried) return _rapid;
            _rapidTried = true;
            var dir = RapidModelsDir;
            if (dir is null) return null;
            try
            {
                var ocr = new RapidOcr();
                ocr.InitModels(new RapidOcrModelSet
                {
                    DetModelPath = Path.Combine(dir, "PP-OCRv6_det_tiny.onnx"),
                    ClsModelPath = Path.Combine(dir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                    RecModelPath = Path.Combine(dir, "PP-OCRv6_rec_tiny.onnx"),
                    KeysPath = Path.Combine(dir, "ppocrv6_tiny_dict.txt"),
                    DetMean = [127.5F, 127.5F, 127.5F],
                    DetStd = [127.5F, 127.5F, 127.5F],
                });
                _rapid = ocr;
            }
            catch (Exception ex)
            {
                Log.Warn("初始化 RapidOCR 失败：" + ex.Message);
            }
            return _rapid;
        }
    }

    /// <summary>
    /// RapidOCR 识别路径：像素进 ONNX，按检测框坐标重组行，再走与系统 OCR
    /// 相同的归一化后处理。多语言模型一次出中英日韩，不按 preferred 选语言。
    /// </summary>
    static async Task<string> RecognizeRapidAsync(CapturedImage image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var engine = RapidEngine();
        if (engine is null)
            throw new InvalidOperationException(
                "RapidOCR 模型没就位：" + (RapidModelsHint() ?? "初始化失败") +
                "。去「设置 → 文字识别」改回系统 OCR，或补上模型文件。");

        using var bmp = ToSkiaBitmap(image);
        ct.ThrowIfCancellationRequested();
        // Detect 是 CPU 密集的同步调用，挪到线程池免得卡住界面线程。
        var result = await Task.Run(() => engine.Detect(bmp, RapidOcrOptions.PPOCRv6), ct)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var tokens = new List<Token>();
        foreach (var block in result.TextBlocks)
        {
            var text = block.Text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            var pts = block.BoxPoints;
            var left = pts.Min(p => p.X);
            var top = pts.Min(p => p.Y);
            var right = pts.Max(p => p.X);
            var bottom = pts.Max(p => p.Y);
            tokens.Add(new Token(text, left, top,
                Math.Max(1, right - left), Math.Max(1, bottom - top), tokens.Count));
        }
        if (tokens.Count == 0) return "";

        // 识别框已经是整行级别，直接用坐标重组段落（空行、缩进和间距与系统 OCR 一致）。
        return NormalizeOcrText(CleanCandidate(LayoutTokens(tokens, cjk: true, rightToLeft: false)));
    }

    /// <summary>把 BGRA32 像素搬进 SKBitmap（SkiaSharp 原生 BGRA 序，行宽一致直接拷）。</summary>
    static SKBitmap ToSkiaBitmap(CapturedImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        try
        {
            Marshal.Copy(image.Pixels, 0, bmp.GetPixels(), image.Pixels.Length);
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }
}