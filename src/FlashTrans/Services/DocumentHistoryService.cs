using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlashTrans.Services;

public sealed record DocumentHistoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public string SourcePath { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public string ProviderName { get; init; } = "";
    public string SourceLanguage { get; init; } = "";
    public string TargetLanguage { get; init; } = "";
    public string Status { get; init; } = "Completed";
    public int Completed { get; init; }
    public int Total { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<DocumentHistoryEntry>))]
internal partial class DocumentHistoryJson : JsonSerializerContext;

/// <summary>只存元信息，不记录正文、请求或服务端错误。清空记录永远不会删除用户文档。</summary>
public sealed class DocumentHistoryService
{
    public const int Limit = 100;
    readonly string _path;
    internal string DirectoryPath => Path.GetDirectoryName(_path)!;
    readonly object _gate = new();
    public string? LoadWarning { get; private set; }
    public DocumentHistoryService(string directory) => _path = Path.Combine(directory, "document-history.json");

    public IReadOnlyList<DocumentHistoryEntry> Load()
    {
        lock (_gate)
        {
            LoadWarning = null;
            try
            {
                if (!File.Exists(_path)) return [];
                if (new FileInfo(_path).Length > 4 * 1024 * 1024) throw new InvalidDataException();
                var entries = JsonSerializer.Deserialize(File.ReadAllText(_path), DocumentHistoryJson.Default.ListDocumentHistoryEntry)
                    ?? throw new InvalidDataException();
                if (entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.SourcePath) || !Path.IsPathFullyQualified(e.SourcePath)
                    || string.IsNullOrWhiteSpace(e.Id) || e.TargetLanguage is null || e.SourceLanguage is null
                    || e.OutputPath is null || e.ProviderName is null || e.Status is null))
                    throw new InvalidDataException();
                return entries.OrderByDescending(e => e.Time).Take(Limit).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LoadWarning = "历史记录无法读取。原记录已保留，可清空后重新记录。";
                return [];
            }
        }
    }

    public void Add(DocumentHistoryEntry entry)
    {
        lock (_gate)
        {
            var entries = Load();
            if (LoadWarning is not null) throw new IOException(LoadWarning);
            Write(entries.Where(e => e.Id != entry.Id).Prepend(entry).OrderByDescending(e => e.Time).Take(Limit).ToList());
        }
    }

    public void Clear()
    {
        lock (_gate) { Write([]); LoadWarning = null; }
    }

    void Write(List<DocumentHistoryEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, DocumentHistoryJson.Default.ListDocumentHistoryEntry));
            File.Move(temp, _path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
