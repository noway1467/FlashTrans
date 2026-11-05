using System.IO;
using System.Text.Json;
using FlashTrans.Core;

namespace FlashTrans.Services;

internal sealed class DesktopShelfRecord
{
    public DesktopShelfEntry Entry { get; set; } = new();
    public string State { get; set; } = "importing";
}

/// <summary>每项独立目录及操作记录；即使 settings.json 保存失败，也能从实际收纳目录恢复列表。</summary>
public sealed class DesktopShelfStorage(string root)
{
    public static DesktopShelfStorage Default { get; } = new(Path.Combine(SettingsService.Instance.ConfigDir, "desktop-shelf"));
    public string Root { get; } = FullPath(root);
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<(int Added, string? Error)> ImportAsync(DesktopShelfSettings settings, DesktopShelfBox box, IEnumerable<string> paths)
    {
        var selected = paths.Take(QuickLaunchService.MaxItems + 1).ToArray();
        var recoveryError = await RecoverAsync(settings);
        if (recoveryError is not null) return (0, recoveryError);
        await _gate.WaitAsync();
        try
        {
            // 去重和原位置重叠检查覆盖所有收纳窗；数量上限只按目标收纳窗计算。
            var existing = DesktopShelfService.AllItems(settings).ToArray();
            var room = QuickLaunchService.MaxItems - box.Items.Count;
            var result = await Task.Run(() => Import(selected, existing, room));
            // 移动期间目标收纳窗被删除时放进第一个收纳窗，不让已移入的文件在列表里消失。
            if (!settings.Boxes.Contains(box)) box = FirstBox(settings);
            foreach (var entry in result.Entries)
            {
                var owner = settings.Boxes.FirstOrDefault(b => b.Items.Any(i => !i.IsStored && Same(i.Path, entry.OriginalPath)));
                if (owner is null) { box.Items.Add(entry); continue; }
                // 旧入口原位转换，仍留在它原来所在的收纳窗。
                var old = owner.Items.FindIndex(i => !i.IsStored && Same(i.Path, entry.OriginalPath));
                entry.Name = owner.Items[old].Name;
                owner.Items[old] = entry;
            }
            return (result.Entries.Count, result.Error);
        }
        finally { _gate.Release(); }
    }

    (List<DesktopShelfEntry> Entries, string? Error) Import(string[] paths, DesktopShelfEntry[] existing, int room)
    {
        var entries = new List<DesktopShelfEntry>();
        var added = 0;
        string? error = null;
        CheckNoLinks(Root);
        Directory.CreateDirectory(Root);
        foreach (var input in paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p?.Length ?? 0))
        {
            DesktopShelfRecord? record = null;
            var replacing = false;
            try
            {
                var source = FullPath(input);
                if (entries.Any(i => i.IsFolder && Inside(source, i.OriginalPath))) continue;
                if (existing.Any(i => i.IsStored && (Same(i.Path, source) || Same(i.OriginalPath, source)))
                    || entries.Any(i => Same(i.OriginalPath, source))) throw new IOException("该项目已收纳，请先解除收纳再添加");
                replacing = existing.Any(i => !i.IsStored && Same(i.Path, source));
                if (!replacing && added >= room)
                    throw new IOException($"每个收纳窗最多 {QuickLaunchService.MaxItems} 项，请先解除不再使用的项目");
                ValidateUserPath(source);
                var candidate = new AppSettings();
                if (!QuickLaunchService.TryAdd(candidate, source, Directory.Exists(source), out var why)) throw new IOException(why);
                var item = candidate.QuickLaunchItems[0];
                if (existing.Any(i => i.IsStored && ((item.IsFolder && Inside(i.OriginalPath, source))
                    || (i.IsFolder && Inside(source, i.OriginalPath)))))
                    throw new IOException("该目录与已收纳项目的原位置重叠，请先还原相关项目再收纳整个目录");
                if (item.IsFolder) CheckTree(source);
                var entry = new DesktopShelfEntry { StorageId = Guid.NewGuid().ToString("N"), OriginalPath = source,
                    Name = item.Name, IsFolder = item.IsFolder, AddedAt = DateTimeOffset.UtcNow };
                entry.Path = StoredPath(entry);
                record = new DesktopShelfRecord { Entry = entry };
                WriteRecord(record);
                Move(source, entry.Path, entry.IsFolder);
                record.State = "stored";
                WriteRecord(record);
                entries.Add(entry);
                if (!replacing) added++;
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                error = ex.Message;
                // 跨盘目录移动可能部分成功。保留已移入的文件和日志，绝不靠回滚覆盖或删除用户文件。
                if (record is not null && Exists(record.Entry.Path))
                {
                    entries.Add(record.Entry);
                    if (!replacing) added++;
                    error += "；已移动的部分保留在收纳目录，原位置也请检查";
                }
            }
        }
        return (entries, error);
    }

    public async Task RestoreAsync(DesktopShelfSettings settings, DesktopShelfEntry item)
    {
        await _gate.WaitAsync();
        try
        {
            if (item.IsStored)
                await Task.Run(() =>
                {
                    var record = ReadRecord(item.StorageId);
                    if (!Same(record.Entry.OriginalPath, item.OriginalPath)) throw new IOException("还原记录与列表不一致，请重新打开收纳窗后再试");
                    var source = StoredPath(record.Entry);
                    var destination = FullPath(record.Entry.OriginalPath);
                    ValidateUserPath(destination);
                    if (Exists(destination)) throw new IOException("原位置已有同名文件或文件夹，未覆盖；请先移走同名项目，再解除收纳");
                    if (!Exists(source)) throw new IOException("收纳文件找不到，未移除记录；请打开收纳目录检查");
                    if (record.Entry.IsFolder) CheckTree(source);
                    record.State = "restoring";
                    WriteRecord(record);
                    Move(source, destination, record.Entry.IsFolder);
                    record.State = "restored";
                    WriteRecord(record);
                    // 保留已还原标记：即使此后 settings.json 保存失败，下次也不会重现失效的收纳项。
                });
            foreach (var box in settings.Boxes)
                if (item.IsStored) box.Items.RemoveAll(i => i.StorageId == item.StorageId);
                else box.Items.Remove(item);
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> RecoverAsync(DesktopShelfSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            var result = await Task.Run(ReadInventory);
            foreach (var entry in result.Entries)
            {
                var known = DesktopShelfService.AllItems(settings).FirstOrDefault(i => i.StorageId == entry.StorageId);
                if (known is not null)
                {
                    known.Path = entry.Path;
                    known.OriginalPath = entry.OriginalPath;
                    known.IsFolder = entry.IsFolder;
                    continue;
                }
                // 迁移中途退出时，旧入口仍可能留在配置中；放回旧入口所在的收纳窗，找不到就放进第一个。
                var target = settings.Boxes.FirstOrDefault(b => b.Items.Any(i => !i.IsStored && Same(i.Path, entry.OriginalPath)))
                    ?? FirstBox(settings);
                foreach (var box in settings.Boxes) box.Items.RemoveAll(i => !i.IsStored && Same(i.Path, entry.OriginalPath));
                target.Items.Add(entry);
            }
            foreach (var id in result.Restored)
                foreach (var box in settings.Boxes) box.Items.RemoveAll(i => i.StorageId == id);
            return result.Error;
        }
        finally { _gate.Release(); }
    }

    /// <summary>尚未归一化的配置可能还没有收纳窗，恢复和导入时补一个默认的。</summary>
    internal static DesktopShelfBox FirstBox(DesktopShelfSettings settings)
    {
        if (settings.Boxes.Count == 0) DesktopShelfService.Normalize(settings);
        return settings.Boxes[0];
    }

    (List<DesktopShelfEntry> Entries, List<string> Restored, string? Error) ReadInventory()
    {
        var entries = new List<DesktopShelfEntry>();
        var restored = new List<string>();
        string? error = null;
        CheckNoLinks(Root);
        if (!Directory.Exists(Root)) return (entries, restored, null);
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try
            {
                var record = ReadRecord(id);
                var item = record.Entry;
                item.Path = StoredPath(item);
                if (Exists(item.Path))
                {
                    if (record.State == "importing" && !Exists(item.OriginalPath))
                    {
                        record.State = "stored";
                        WriteRecord(record);
                    }
                    entries.Add(item);
                    if (record.State != "stored") error = "发现未完成的移动，文件保留在收纳目录；请同时检查原位置";
                }
                else if (record.State == "restored" || (record.State == "restoring" && Exists(item.OriginalPath)))
                {
                    restored.Add(id);
                }
                else if (record.State == "importing" && Exists(item.OriginalPath)) CleanEmptyRecord(item);
                else error = "部分收纳文件找不到，记录未删除；请打开收纳目录检查";
            }
            catch (Exception ex) when (IsFileError(ex)) { error = "读取收纳记录失败：" + ex.Message; }
        }
        return (entries, restored, error);
    }

    internal string StoredPath(DesktopShelfEntry entry)
    {
        var directory = EntryDirectory(entry.StorageId);
        var name = Path.GetFileName(FullPath(entry.OriginalPath));
        if (string.IsNullOrEmpty(name)) throw new IOException("收纳记录缺少文件名");
        return Within(Path.Combine(directory, "files", name), directory);
    }

    string EntryDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("收纳记录编号无效");
        return Within(Path.Combine(Root, id), Root);
    }

    DesktopShelfRecord ReadRecord(string id)
    {
        var path = Path.Combine(EntryDirectory(id), "entry.json");
        CheckNoLinks(path);
        var record = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJson.Default.DesktopShelfRecord)
            ?? throw new IOException("收纳记录为空");
        if (record.Entry is null || record.Entry.StorageId != id) throw new IOException("收纳记录编号不一致");
        _ = StoredPath(record.Entry);
        return record;
    }

    void WriteRecord(DesktopShelfRecord record)
    {
        var directory = EntryDirectory(record.Entry.StorageId);
        CheckNoLinks(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "entry.json");
        var temp = path + ".tmp";
        CheckNoLinks(path); CheckNoLinks(temp);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, record, SettingsJson.Default.DesktopShelfRecord);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    void CleanEmptyRecord(DesktopShelfEntry entry)
    {
        var directory = EntryDirectory(entry.StorageId);
        CheckNoLinks(directory);
        var payload = Within(Path.Combine(directory, "files"), directory);
        if (Directory.Exists(payload))
        {
            if (Directory.EnumerateFileSystemEntries(payload).Any()) return;
            Directory.Delete(payload);
        }
        File.Delete(Path.Combine(directory, "entry.json"));
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    void ValidateUserPath(string path)
    {
        CheckNoLinks(path);
        if (Same(path, Path.GetPathRoot(path)!) || Same(path, Root) || Inside(path, Root) || Inside(Root, path))
            throw new IOException("不能移动磁盘根目录、收纳目录或它的上级目录");
        foreach (var special in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            var reserved = Environment.GetFolderPath(special);
            if (reserved.Length > 0 && Same(path, reserved)) throw new IOException("不能整体收纳系统目录或桌面，请选择里面的文件");
            if (reserved.Length > 0 && (special is Environment.SpecialFolder.Windows or Environment.SpecialFolder.ProgramFiles or Environment.SpecialFolder.ProgramFilesX86)
                && Inside(path, reserved)) throw new IOException("不能移动系统或已安装程序的文件，请收纳它的桌面快捷方式");
        }
        if (Inside(path, AppContext.BaseDirectory) || Same(path, AppContext.BaseDirectory)
            || Inside(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), path))
            throw new IOException("不能移动当前正在使用的闪译程序目录；程序请优先收纳桌面快捷方式");
    }

    static void Move(string source, string destination, bool folder)
    {
        source = FullPath(source); destination = FullPath(destination);
        CheckNoLinks(source); CheckNoLinks(destination);
        if (Exists(destination)) throw new IOException("目标位置已存在同名项目，未覆盖");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!folder) File.Move(source, destination, overwrite: false);
        else if (Same(Path.GetPathRoot(source)!, Path.GetPathRoot(destination)!)) Directory.Move(source, destination);
        else
            // 使用框架提供的跨盘目录移动，不自行递归删除源目录；冲突禁止覆盖，部分完成由记录恢复。
            Microsoft.VisualBasic.FileIO.FileSystem.MoveDirectory(source, destination, overwrite: false);
        if (Exists(source) || !Exists(destination)) throw new IOException("移动未完整完成，请检查原位置和收纳目录");
    }

    static void CheckTree(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            _ = Within(path, root);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("文件夹内含链接或云占位文件，请先转为本地文件再收纳");
            if ((attributes & FileAttributes.Directory) != 0) CheckTree(path);
        }
    }

    static void CheckNoLinks(string path)
    {
        for (var current = FullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("路径包含链接或云占位文件，请先转为本地文件再收纳");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    static string Within(string path, string parent)
    {
        var full = FullPath(path);
        if (!Inside(full, parent)) throw new IOException("收纳路径越界，已停止文件操作");
        CheckNoLinks(full);
        return full;
    }

    static string FullPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("需要完整文件路径");
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.AsSpan(path.Length >= 2 && path[1] == ':' ? 2 : 0).Contains(':'))
            throw new IOException("不支持设备路径或备用数据流路径");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    static bool Same(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
    static bool Inside(string path, string parent) => path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException;
}
