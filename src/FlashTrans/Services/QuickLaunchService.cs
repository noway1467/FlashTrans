using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using FlashTrans.Core;

namespace FlashTrans.Services;

public enum QuickLaunchKind { Program, Folder, File }

/// <summary>仅处理显式选择的文件系统路径，不执行命令行、URL 或自动扫描程序。</summary>
public static class QuickLaunchService
{
    public const int MaxItems = 200;

    // 普通文档交给系统默认程序打开；脚本、安装包这类「打开即执行」的类型不收，
    // 免得一次误触就跑了一段命令。需要时仍可添加 .exe 本体或用户自己建的快捷方式。
    static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".com", ".scr", ".pif", ".cpl", ".msi", ".msp", ".msc", ".ps1", ".psm1", ".psd1",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".jar", ".reg", ".inf", ".scf", ".url",
        ".website", ".application", ".appref-ms", ".gadget", ".settingcontent-ms",
    };

    static string CanonicalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择完整的程序、文件或文件夹路径");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    static bool IsProgram(string path) => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".lnk";
    static bool IsAllowedFile(string path) => Path.GetExtension(path) is var ext && !BlockedExtensions.Contains(ext);

    public static QuickLaunchKind KindOf(QuickLaunchEntry item) => item.IsFolder ? QuickLaunchKind.Folder
        : IsProgram(item.Path) ? QuickLaunchKind.Program : QuickLaunchKind.File;

    public static string KindName(QuickLaunchEntry item) => KindOf(item) switch
    {
        QuickLaunchKind.Folder => "文件夹",
        QuickLaunchKind.Program => "程序",
        _ => "文件",
    };

    /// <summary>只有 exe/快捷方式能提权；文件夹和文档提权没有意义，还会被资源管理器拒绝。</summary>
    public static bool CanRunAsAdmin(QuickLaunchEntry item) => KindOf(item) == QuickLaunchKind.Program;

    public static string DefaultName(string path, bool folder)
    {
        var name = folder ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    public static List<QuickLaunchEntry> Normalize(IEnumerable<QuickLaunchEntry>? items)
    {
        var result = new List<QuickLaunchEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items ?? [])
        {
            if (item is null) continue;
            try
            {
                var path = CanonicalPath(item.Path);
                if ((!item.IsFolder && !IsAllowedFile(path)) || !seen.Add(path)) continue;
                var name = (item.Name ?? "").Trim();
                if (name.Length == 0) name = DefaultName(path, item.IsFolder);
                result.Add(new QuickLaunchEntry
                {
                    Path = path, Name = name[..Math.Min(name.Length, 80)], IsFolder = item.IsFolder,
                    LaunchCount = Math.Max(0, item.LaunchCount), LastLaunched = item.LastLaunched,
                });
                if (result.Count == MaxItems) break;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        // 不检查磁盘是否在线：移动硬盘/网络文件夹暂时不可用时不能丢掉用户收藏。
        return result;
    }

    public static bool TryAdd(AppSettings settings, string path, bool folder, out string? error)
    {
        error = null;
        try
        {
            path = CanonicalPath(path);
            ValidateTarget(path, folder);
            if (settings.QuickLaunchItems.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("这个项目已经在快速启动列表中");
            if (settings.QuickLaunchItems.Count >= MaxItems)
                throw new InvalidOperationException($"最多添加 {MaxItems} 项，请先移除不再使用的项目");
            settings.QuickLaunchItems.Add(new QuickLaunchEntry { Name = DefaultName(path, folder), Path = path, IsFolder = folder });
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>拖放进来的路径自己判断是文件夹还是文件；返回成功数和最后一个错误。</summary>
    public static (int Added, string? Error) TryAddPaths(AppSettings settings, IEnumerable<string> paths)
    {
        var added = 0;
        string? lastError = null;
        foreach (var path in paths)
        {
            if (TryAdd(settings, path, Directory.Exists(path), out var error)) added++;
            else lastError = error;
        }
        return (added, lastError);
    }

    static void ValidateTarget(string path, bool folder)
    {
        if (folder)
        {
            if (!Directory.Exists(path)) throw new IOException("文件夹不存在或不可访问，请检查路径或重新添加");
        }
        else
        {
            if (!IsAllowedFile(path)) throw new ArgumentException("不支持脚本、安装包等直接执行的文件；可添加程序（.exe）、快捷方式或普通文件");
            if (!File.Exists(path)) throw new IOException("文件不存在或不可访问，请检查路径或重新添加");
        }
    }

    internal static ProcessStartInfo CreateStartInfo(QuickLaunchEntry item, bool asAdmin = false)
    {
        var path = CanonicalPath(item.Path);
        ValidateTarget(path, item.IsFolder);
        if (asAdmin && !CanRunAsAdmin(item)) throw new InvalidOperationException("只有程序和快捷方式可以以管理员身份运行");
        return new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = asAdmin ? "runas" : "",
            // 只给 exe 设工作目录：快捷方式有自己配置的起始位置，文档交给关联程序决定。
            WorkingDirectory = KindOf(item) == QuickLaunchKind.Program && !Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(path) ?? "" : "",
        };
    }

    /// <summary>启动成功才计数；返回 false 表示用户在 UAC 提示里点了「否」。</summary>
    public static bool Launch(QuickLaunchEntry item, bool asAdmin = false)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo(item, asAdmin));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; } // ERROR_CANCELLED
        item.LaunchCount = item.LaunchCount >= int.MaxValue - 1 ? item.LaunchCount : item.LaunchCount + 1;
        item.LastLaunched = DateTimeOffset.Now;
        return true;
    }

    /// <summary>目标文件或文件夹已不存在的项目；会访问磁盘，调用方放后台线程。</summary>
    public static List<QuickLaunchEntry> FindMissing(IEnumerable<QuickLaunchEntry> items) =>
        items.Where(i => !(i.IsFolder ? Directory.Exists(i.Path) : File.Exists(i.Path))).ToList();

    /// <summary>在资源管理器里选中目标；目标已不在时退到最近一层仍存在的上级目录。</summary>
    public static void OpenLocation(QuickLaunchEntry item)
    {
        var path = CanonicalPath(item.Path);
        if (File.Exists(path) || Directory.Exists(path))
        {
            // explorer 自己解析参数，只认 /select,"路径" 这种写法；Windows 路径里不可能出现引号。
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var _ = Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false });
            return;
        }
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (!Directory.Exists(parent)) continue;
            using var _ = Process.Start(new ProcessStartInfo(parent) { UseShellExecute = true });
            return;
        }
        throw new IOException("所在位置已不存在或不可访问");
    }

    public static IEnumerable<QuickLaunchEntry> Filter(IEnumerable<QuickLaunchEntry> items, string query) => Search(items, query);

    /// <summary>
    /// 每个词都要命中名称、路径或名称首字母（「vsc」→ Visual Studio Code）。
    /// 有搜索词时按匹配质量排序，同分再看启动次数；没有搜索词时保持用户手动排好的顺序。
    /// </summary>
    public static IEnumerable<QuickLaunchEntry> Search(IEnumerable<QuickLaunchEntry> items, string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return items;
        return items.Select((item, index) => (item, index, score: Score(item, terms)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.item.LaunchCount)
            .ThenBy(x => x.index)
            .Select(x => x.item);
    }

    static int Score(QuickLaunchEntry item, string[] terms)
    {
        var total = 0;
        var initials = Initials(item.Name);
        foreach (var term in terms)
        {
            int score;
            if (item.Name.Equals(term, StringComparison.OrdinalIgnoreCase)) score = 100;
            else if (item.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) score = 80;
            else if (term.Length >= 2 && initials.StartsWith(term, StringComparison.OrdinalIgnoreCase)) score = 70;
            else if (WordStarts(item.Name).Any(w => item.Name.AsSpan(w).StartsWith(term, StringComparison.OrdinalIgnoreCase))) score = 60;
            else if (item.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) score = 40;
            else if (item.Path.Contains(term, StringComparison.OrdinalIgnoreCase)) score = 10;
            else return 0;
            total += score;
        }
        return total;
    }

    /// <summary>空格、横线、点号后以及小写转大写处算词首，兼顾「Visual Studio」和「OneNote」。</summary>
    static IEnumerable<int> WordStarts(string name)
    {
        for (var i = 0; i < name.Length; i++)
        {
            if (!char.IsLetterOrDigit(name[i])) continue;
            if (i == 0 || !char.IsLetterOrDigit(name[i - 1]) || (char.IsUpper(name[i]) && char.IsLower(name[i - 1])))
                yield return i;
        }
    }

    static string Initials(string name) => string.Concat(WordStarts(name).Select(i => name[i]));
}
