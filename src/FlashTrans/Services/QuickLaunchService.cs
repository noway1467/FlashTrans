using System.Diagnostics;
using System.IO;
using FlashTrans.Core;

namespace FlashTrans.Services;

/// <summary>仅处理显式选择的文件系统路径，不执行命令行、URL 或自动扫描程序。</summary>
public static class QuickLaunchService
{
    public const int MaxItems = 200;

    static string CanonicalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择完整的程序或文件夹路径");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    static bool IsProgram(string path) => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".lnk";

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
                if ((!item.IsFolder && !IsProgram(path)) || !seen.Add(path)) continue;
                var name = (item.Name ?? "").Trim();
                if (name.Length == 0) name = DefaultName(path, item.IsFolder);
                result.Add(new QuickLaunchEntry { Path = path, Name = name[..Math.Min(name.Length, 80)], IsFolder = item.IsFolder });
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

    static void ValidateTarget(string path, bool folder)
    {
        if (folder)
        {
            if (!Directory.Exists(path)) throw new IOException("文件夹不存在或不可访问，请检查路径或重新添加");
        }
        else
        {
            if (!IsProgram(path)) throw new ArgumentException("仅支持程序（.exe）或快捷方式（.lnk）");
            if (!File.Exists(path)) throw new IOException("程序不存在或不可访问，请检查路径或重新添加");
        }
    }

    internal static ProcessStartInfo CreateStartInfo(QuickLaunchEntry item)
    {
        var path = CanonicalPath(item.Path);
        ValidateTarget(path, item.IsFolder);
        return new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            // 不给 .lnk 强行覆盖工作目录，保留快捷方式自己配置的参数和起始位置。
            WorkingDirectory = item.IsFolder || Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                ? "" : Path.GetDirectoryName(path) ?? "",
        };
    }

    public static void Launch(QuickLaunchEntry item)
    {
        using var process = Process.Start(CreateStartInfo(item));
    }

    public static IEnumerable<QuickLaunchEntry> Filter(IEnumerable<QuickLaunchEntry> items, string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return items.Where(i => terms.All(t => i.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
            || i.Path.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }
}
