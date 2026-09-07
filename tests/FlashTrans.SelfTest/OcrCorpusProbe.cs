using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.SelfTest;

/// <summary>
/// 显式 --ocr-corpus 才联网下载公开图片，正常自测不依赖外网。
/// 固定 Git blob 哈希；只下载数据，不安装引擎、不上传截图，也不把错题伪装成通过。
/// </summary>
static class OcrCorpusProbe
{
    readonly record struct Sample(string Id, string Repo, string Path, string Blob, string Expected);
    const string PhotoText = "This is a lot of 12 point text to test the\n"
        + "ocr code and see if it works on all types\nof file format.\n\n"
        + "The quick brown dog jumped over the\nlazy fox. The quick brown dog jumped\n"
        + "over the lazy fox. The quick brown dog\njumped over the lazy fox. The quick\n"
        + "brown dog jumped over the lazy fox.\n";

    // 英文段落真值：tesseract-ocr/test testing/phototest.txt，blob 02d3a77cbb52326e0a1e5eef78f8e28f20eb122b。
    // 其余短行真值人工对照原图录入，不来自本程序自己的 OCR 输出。
    static readonly Sample[] Samples =
    [
        new("phototest", "tesseract-ocr/test", "testing/phototest.tif",
            "adac046dba073285f945446d5ad557894da82c9b", PhotoText),
        new("phototest-rot90", "tesseract-ocr/test", "testing/phototestrot.tif",
            "ff845df60453daa3d4ee91f0385d18db8b8b074a", PhotoText),
        new("digits-12", "tesseract-ocr/test", "testing/12.tif",
            "4e4eb023e43e6a49fa6fa42feb895e6211be0450", "12"),
        new("ch-doc-1", "PaddlePaddle/PaddleOCR", "docs/datasets/images/ch_doc1.jpg",
            "53534400ab5b5cf0eb6291fc3bb483c4e2fa0406", "如，和对旅游表演形式"),
        new("ch-doc-3", "PaddlePaddle/PaddleOCR", "docs/datasets/images/ch_doc3.jpg",
            "c0c2053643c6211b9c2017e305c5fa05bba0cc66", "4年工作报告》中指出"),
        new("en-word-10", "PaddlePaddle/PaddleOCR", "deploy/avh/imgs_words_en/word_10.png",
            "07370f757ea83d5e3c5b1f7498b1f95d3aec2d18", "PAIN"),
    ];

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    static async Task RunAsync()
    {
        if (!OcrService.IsAvailable) throw new InvalidOperationException(OcrService.NoEngineHint());
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FlashTrans-SelfTest/1.0");
        Console.WriteLine("       已安装 OCR 语言：" + string.Join(", ", OcrService.AvailableLanguages));
        Console.WriteLine("       CER：NFC、全角 ASCII 折叠、忽略空白，保留大小写与其余符号。");
        var cache = Path.GetFullPath(Path.Combine("shots", "ocr-corpus-cache"));
        Directory.CreateDirectory(cache);
        var results = new List<object>();
        foreach (var sample in Samples)
        {
            // 重跑复用经过哈希验证的原图，避免反复下载触发公共 API 限流。
            var cachedImage = Path.Combine(cache, sample.Id + Path.GetExtension(sample.Path));
            var cached = File.Exists(cachedImage);
            byte[] bytes;
            if (cached) bytes = await File.ReadAllBytesAsync(cachedImage).ConfigureAwait(false);
            else
            {
                var url = $"https://api.github.com/repos/{sample.Repo}/git/blobs/{sample.Blob}";
                using var json = JsonDocument.Parse(await client.GetStringAsync(url).ConfigureAwait(false));
                bytes = Convert.FromBase64String(json.RootElement.GetProperty("content").GetString()!);
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(Encoding.UTF8.GetBytes($"blob {bytes.Length}\0"));
            hash.AppendData(bytes);
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sample.Blob, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"公开样例 {sample.Id} 的来源哈希不符");
            if (!cached) await File.WriteAllBytesAsync(cachedImage, bytes).ConfigureAwait(false);

            using var stream = new MemoryStream(bytes);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var timer = Stopwatch.StartNew();
            var text = await OcrService.RecognizeAsync(
                new CapturedImage(bitmap.PixelWidth, bitmap.PixelHeight, pixels), null).ConfigureAwait(false);
            timer.Stop();
            var expected = Normalize(sample.Expected);
            var errors = Distance(expected, Normalize(text));
            var cer = (double)errors / Math.Max(1, expected.Length);
            Console.WriteLine($"       {sample.Id,-18} {errors}/{expected.Length} 字符错误，CER {cer:P2}，{timer.ElapsedMilliseconds} ms");
            results.Add(new
            {
                sample.Id, source = $"https://github.com/{sample.Repo}/blob/main/{sample.Path}",
                imageBlob = sample.Blob, sample.Expected, actual = text,
                errors, referenceCharacters = expected.Length, cer, elapsedMs = timer.ElapsedMilliseconds,
            });
        }
        var report = Path.GetFullPath(Path.Combine("shots", "ocr-corpus-results.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            measuredAtUtc = DateTimeOffset.UtcNow, languages = OcrService.AvailableLanguages,
            metric = "Unicode CER; NFC + fullwidth ASCII folding; whitespace ignored; case-sensitive",
            results,
        }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8).ConfigureAwait(false);
        Console.WriteLine("       详细评测结果：" + report);
    }

    static Rune[] Normalize(string text) => new string(text.Normalize(NormalizationForm.FormC)
        .Select(c => c is >= '\uFF01' and <= '\uFF5E' ? (char)(c - 0xFEE0) : c)
        .Where(c => !char.IsWhiteSpace(c)).ToArray()).EnumerateRunes().ToArray();

    static int Distance(Rune[] expected, Rune[] actual)
    {
        var previous = Enumerable.Range(0, actual.Length + 1).ToArray();
        for (var i = 1; i <= expected.Length; i++)
        {
            var current = new int[actual.Length + 1];
            current[0] = i;
            for (var j = 1; j <= actual.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[^1];
    }
}
