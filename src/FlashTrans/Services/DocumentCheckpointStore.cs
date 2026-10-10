using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlashTrans.Services;

/// <summary>每段一条加密日志，避免每译一段重写整本书；仅最后一条未完成写入可安全丢弃。</summary>
public sealed class DocumentCheckpointStore(string configDirectory)
{
    readonly string _directory = Path.Combine(configDirectory, "document-checkpoints");
    internal Session Open(TranslationDocument document, string identity)
    {
        Directory.CreateDirectory(_directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "v1|" + document.SourceHash + "|" + document.BatchCharacters + "|" + identity)));
        return new Session(Path.Combine(_directory, key + ".checkpoint"), document);
    }

    public int Clear()
    {
        if (!Directory.Exists(_directory)) return 0;
        var files = Directory.GetFiles(_directory, "*.checkpoint", SearchOption.TopDirectoryOnly);
        foreach (var path in files) File.Delete(path);
        return files.Length;
    }

    internal sealed class Session : IDisposable
    {
        readonly FileStream _stream;
        readonly HashSet<int> _saved = [];
        const int MaxBytes = 64 * 1024 * 1024;
        sealed record Record(int Index, string PartHash, string[] Values);
        static string PartHash(DocumentUnit unit) => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(unit.Parts.Select(p => p.Text)))));

        internal Session(string path, TranslationDocument document)
        {
            _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                if (_stream.Length > MaxBytes) throw new InvalidDataException("续译进度超过 64 MB，请清理后重试。");
                var bytes = new byte[(int)_stream.Length]; _stream.ReadExactly(bytes);
                var completeLength = Array.LastIndexOf(bytes, (byte)'\n') + 1;
                var text = Encoding.UTF8.GetString(bytes, 0, completeLength);
                var records = new Dictionary<int, string[]>();
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.StartsWith("dpapi:", StringComparison.Ordinal)) throw new InvalidDataException("进度不是加密格式。");
                    var plain = Dpapi.Unprotect(line);
                    if (plain.Length == 0) throw new InvalidDataException("进度无法解密，请使用原 Windows 账户，或清理后重新翻译。");
                    var record = JsonSerializer.Deserialize<Record>(plain) ?? throw new InvalidDataException("进度记录损坏。");
                    if (record.Index < 0 || record.Index >= document.Count ||
                        record.PartHash != PartHash(document.Units[record.Index])) throw new InvalidDataException("进度与原文不匹配。");
                    records[record.Index] = document.Units[record.Index].ValidateValues(record.Values);
                }
                // 全部验证通过后才回填；中间损坏不能当成成功恢复。
                foreach (var (index, values) in records)
                {
                    document.Units[index].Apply(values); _saved.Add(index);
                }
                if (completeLength < bytes.Length)
                {
                    _stream.SetLength(completeLength);
                    document.CheckpointWarning = "上次进度末条写入未完成，已保留完整段落；最后一段会重新翻译。";
                }
                _stream.Position = _stream.Length;
            }
            catch { _stream.Dispose(); throw; }
        }

        internal void Save(int index, DocumentUnit unit, string[] values)
        {
            if (_saved.Contains(index)) return;
            var plain = JsonSerializer.Serialize(new Record(index, PartHash(unit), values));
            var encrypted = Dpapi.Protect(plain);
            // 既有密钥助手有兼容性回退；文档正文绝不允许回退到明文。
            if (!encrypted.StartsWith("dpapi:", StringComparison.Ordinal) || Dpapi.Unprotect(encrypted) != plain)
                throw new IOException("无法加密续译进度，已停止。可关闭保存进度后仅在内存中翻译。");
            var bytes = Encoding.UTF8.GetBytes(encrypted + "\n");
            if (_stream.Length + bytes.Length > MaxBytes) throw new IOException("续译进度达到 64 MB 上限，请拆分文件。");
            var before = _stream.Length;
            try { _stream.Write(bytes); _stream.Flush(flushToDisk: true); }
            catch { _stream.SetLength(before); throw; }
            _saved.Add(index);
        }

        public void Dispose() => _stream.Dispose();
    }
}
