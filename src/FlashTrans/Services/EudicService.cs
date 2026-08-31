using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace FlashTrans.Services;

/// <summary>欧路词典联动：把选中的词发给欧路词典查询。</summary>
public static class EudicService
{
    static string? _cachedPath;
    static bool _probed;

    /// <summary>已安装的欧路词典可执行文件路径（找不到返回 null）。</summary>
    public static string? DetectPath()
    {
        if (_probed) return _cachedPath;
        _probed = true;
        _cachedPath = Probe();
        return _cachedPath;
    }

    static string? Probe()
    {
        try
        {
            foreach (var (root, sub) in new[]
            {
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths\eudic.exe"),
                (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\App Paths\eudic.exe"),
                (Registry.CurrentUser, @"Software\Eudic"),
                (Registry.LocalMachine, @"Software\Eudic"),
            })
            {
                using var key = root.OpenSubKey(sub);
                var v = key?.GetValue(null) as string ?? key?.GetValue("Path") as string;
                if (string.IsNullOrWhiteSpace(v)) continue;
                var path = v.Trim('"');
                if (Directory.Exists(path)) path = Path.Combine(path, "eudic.exe");
                if (File.Exists(path)) return path;
            }

            foreach (var baseDir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            })
            {
                if (string.IsNullOrEmpty(baseDir)) continue;
                foreach (var name in new[] { "eudic", "Eudic", "欧路词典" })
                {
                    var p = Path.Combine(baseDir, name, "eudic.exe");
                    if (File.Exists(p)) return p;
                }
            }
        }
        catch (Exception ex) { Log.Warn("查找欧路词典失败：" + ex.Message); }
        return null;
    }

    public static bool IsAvailable =>
        HasUrlScheme() || DetectPath() is not null ||
        File.Exists(SettingsService.Instance.Current.EudicPath);

    static bool HasUrlScheme()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey("eudic");
            return key is not null;
        }
        catch { return false; }
    }

    /// <summary>在欧路词典中查词。优先用 eudic:// 协议，失败再直接调用 exe。</summary>
    public static bool Lookup(string word)
    {
        word = word.Trim();
        if (word.Length == 0) return false;

        if (HasUrlScheme() && TryStart($"eudic://dict/{Uri.EscapeDataString(word)}", null))
            return true;

        var exe = SettingsService.Instance.Current.EudicPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) exe = DetectPath();
        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            return TryStart(exe, word);

        return false;
    }

    static bool TryStart(string file, string? args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(args)) psi.Arguments = "\"" + args.Replace("\"", "") + "\"";
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"启动欧路词典失败（{file}）：{ex.Message}");
            return false;
        }
    }
}
