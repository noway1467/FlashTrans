using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlashTrans.Core;

namespace FlashTrans.Services;

public sealed record DocumentProgress(int Completed, int Total, string Message);

/// <summary>任务的内存快照；失败/取消可在当前窗口继续，不把正文写入设置或日志。</summary>
public sealed class TranslationDocument
{
    internal readonly List<DocumentUnit> Units = [];
    internal Action<Stream> Write = null!;
    internal Action<string>? Finish;
    internal string? RunIdentity;
    public string SourcePath { get; internal set; } = "";
    public List<string> Warnings { get; } = [];
    public int Count => Units.Count;
    public int Completed => Units.Count(u => u.Result is not null);
    public int CharacterCount => Units.Sum(u => u.Parts.Sum(p => p.Text.Length));
    public bool IsComplete => Count > 0 && Completed == Count;

    public string SaveCopy(string? outputDirectory, string target, CancellationToken ct = default)
    {
        var directory = string.IsNullOrWhiteSpace(outputDirectory) ? Path.GetDirectoryName(SourcePath)! : outputDirectory.Trim();
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("输出目录不存在，请重新选择文件夹。");
        var stem = Path.GetFileNameWithoutExtension(SourcePath);
        if (stem.Length > 100) stem = stem[..100];
        var language = Regex.Replace(target, @"[^a-zA-Z0-9-]", "");
        if (language.Length == 0) language = "translated";
        if (language.Length > 24) language = language[..24];
        var extension = Path.GetExtension(SourcePath);
        for (var i = 1; i <= 10000; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, $"{stem}_译文_{language}{(i == 1 ? "" : $" ({i})")}{extension}");
            if (File.Exists(path) || Directory.Exists(path)) continue;
            try { SaveAs(path, ct); return path; }
            // 检查到写入之间可能有另一个任务创建同名文件，只对名称竞争重试。
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
        throw new IOException("同名译文过多，请更换输出目录。");
    }

    public void SaveAs(string path, CancellationToken ct = default)
    {
        if (!IsComplete) throw new InvalidOperationException("尚未全部翻译完成，不能导出不完整的文档。可点击继续翻译。");
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), Path.GetExtension(SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("输出文件的扩展名必须与原文件一致。");
        if (string.Equals(path, SourcePath, StringComparison.OrdinalIgnoreCase) || File.Exists(path))
            throw new IOException("为保护原文件，不覆盖已有文件。请另选一个新文件名。");
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".flashtrans-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            ct.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) Write(stream);
            ct.ThrowIfCancellationRequested();
            // 同目录完成后再改名；失败、取消或目标被抢先创建时都不动原文件。
            File.Move(temp, path, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal sealed record DocumentPart(string Text, Action<string> Set);

internal sealed class DocumentUnit(List<DocumentPart> parts)
{
    internal List<DocumentPart> Parts { get; } = parts;
    internal string[]? Result;
    readonly string _tag = "FT" + Guid.NewGuid().ToString("N")[..8];
    string Marker(int i) => $"[{_tag}_{i}]";
    internal string Input => Parts.Count == 1 ? Parts[0].Text
        : string.Concat(Parts.Select((p, i) => Marker(i) + p.Text)) + Marker(Parts.Count);

    internal string[] Validate(string text)
    {
        if (Parts.Count == 1) return [Check(text)];
        var result = new string[Parts.Count];
        text = text.Trim();
        if (!text.StartsWith(Marker(0), StringComparison.Ordinal) || !text.EndsWith(Marker(Parts.Count), StringComparison.Ordinal))
            throw new InvalidDataException("翻译源未保留格式标记。请使用能遵循指令的 AI 源，或换源后重新翻译。");
        for (var i = 0; i <= Parts.Count; i++)
        {
            var marker = Marker(i);
            if (text.IndexOf(marker, StringComparison.Ordinal) != text.LastIndexOf(marker, StringComparison.Ordinal))
                throw new InvalidDataException("译文重复了格式标记，已阻止写入文档。");
            if (i == Parts.Count) break;
            var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = text.IndexOf(Marker(i + 1), StringComparison.Ordinal);
            if (start < marker.Length || end < start)
                throw new InvalidDataException("译文缺少格式标记或顺序错误，已阻止写入文档。");
            result[i] = Check(text[start..end]);
            if (result[i].Contains("[" + _tag + "_", StringComparison.Ordinal))
                throw new InvalidDataException("译文出现了额外格式标记，已阻止写入文档。");
        }
        return result;
    }

    static string Check(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("翻译源返回了空段落，已停止，避免漏译。");
        if (text.Length > 100000) throw new InvalidDataException("单段译文异常过长，已停止。");
        try { System.Xml.XmlConvert.VerifyXmlChars(text); }
        catch (System.Xml.XmlException ex) { throw new InvalidDataException("译文包含无效控制字符，已停止以保护文档。", ex); }
        // 结构换行由解析器保留，不让模型额外的换行破坏表格或 Markdown。
        return Regex.Replace(text.Trim(), @"\s*[\r\n]+\s*", " ");
    }
    internal void Apply(string[] values)
    {
        for (var i = 0; i < Parts.Count; i++) Parts[i].Set(values[i]);
        Result = values;
    }
    internal void Reset()
    {
        for (var i = 0; i < Parts.Count; i++) Parts[i].Set(Parts[i].Text);
        Result = null;
    }
}

public static class DocumentTranslation
{
    public const string FileFilter = "可翻译文件|*.epub;*.txt;*.md;*.markdown;*.docx|EPUB 电子书|*.epub|文本|*.txt|Markdown|*.md;*.markdown|Word 文档|*.docx";
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".epub" or ".txt" or ".md" or ".markdown" or ".docx";
    public static TranslationDocument Load(string path, CancellationToken ct = default) => DocumentFormats.Load(path, ct);

    public static Task TranslateAsync(TranslationDocument document, ProviderConfig provider, string from, string target,
        IProgress<DocumentProgress>? progress, CancellationToken ct, int timeoutSeconds = 180)
    {
        var snapshot = provider.Clone();
        snapshot.TimeoutMs = Math.Clamp(timeoutSeconds, 10, 600) * 1000;
        // 独立注册表和快照：不经过自动切源/聚合/历史缓存，设置页也不能改变在途任务。
        var translator = new ProviderRegistry().Get(snapshot);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot))));
        return RunAsync(document, translator, from, target, identity, progress, ct);
    }

    internal static async Task RunAsync(TranslationDocument document, ITranslator translator, string from, string target,
        string identity, IProgress<DocumentProgress>? progress, CancellationToken ct)
    {
        if (document.Count == 0) throw new InvalidOperationException("文件没有可翻译的正文。");
        if (string.IsNullOrWhiteSpace(target) || target == Languages.Auto) throw new ArgumentException("请选择目标语言。");
        if (translator.ConfigError is { } error) throw new InvalidOperationException(error);
        var runIdentity = identity + "|" + from + "|" + target;
        if (document.RunIdentity != runIdentity)
        {
            foreach (var unit in document.Units) unit.Reset();
            document.RunIdentity = runIdentity;
        }
        progress?.Report(new(document.Completed, document.Count, "正在翻译"));
        foreach (var unit in document.Units)
        {
            ct.ThrowIfCancellationRequested();
            if (unit.Result is not null) continue;
            Exception? failure = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = await translator.TranslateAsync(new TranslateRequest
                    {
                        Text = unit.Input, From = from, Targets = [target], WantDictionary = false,
                        Style = "Translate every text fragment. Keep every [FTxxxxxxxx_n] marker exactly once, unchanged and in the original order. " +
                                "Markers delimit formatting spans in the same passage. Do not add explanations, Markdown fences, or new markers."
                    }, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (!result.Ok || string.IsNullOrWhiteSpace(result.Get(target)))
                        throw new InvalidDataException(result.Error ?? "翻译源未返回目标语言译文。");
                    unit.Apply(unit.Validate(result.Get(target)!));
                    failure = null;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is InvalidDataException or ProviderException or HttpRequestException)
                {
                    failure = ex;
                    if (attempt < 2)
                    {
                        progress?.Report(new(document.Completed, document.Count, $"当前段失败，正在重试（{attempt + 1}/2）"));
                        await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct).ConfigureAwait(false);
                    }
                }
            }
            if (failure is not null)
                throw new InvalidOperationException($"第 {document.Completed + 1}/{document.Count} 段失败：{failure.Message} 已完成的段落保留在当前窗口，可继续翻译。", failure);
            progress?.Report(new(document.Completed, document.Count, document.IsComplete ? "翻译完成，可另存文件" : "正在翻译"));
        }
        document.Finish?.Invoke(target);
    }
}
