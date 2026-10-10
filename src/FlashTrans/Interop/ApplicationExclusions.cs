using System.Diagnostics;
using System.IO;
using FlashTrans.Services;

namespace FlashTrans.Interop;

/// <summary>只匹配进程名，不按标题猜测应用，也不使用容易误伤的通配符。</summary>
internal static class ApplicationExclusions
{
    internal static List<string> Normalize(IEnumerable<string>? entries) => (entries ?? [])
        .Select(s => Path.GetFileName((s ?? "").Trim().Trim('"')).Trim())
        .Where(s => s.Length > 0 && s.Length < 260 && s.IndexOfAny(['*', '?', ':']) < 0)
        .Select(s => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : s + ".exe")
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList();

    internal static bool Matches(string processName, IEnumerable<string> entries) =>
        Normalize(entries).Contains(processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName : processName + ".exe", StringComparer.OrdinalIgnoreCase);

    internal static bool IsExcluded(IntPtr window)
    {
        var entries = SettingsService.Instance.Current.ExcludedApplications;
        if (entries.Count == 0 || window == IntPtr.Zero) return false;
        try
        {
            Win32.GetWindowThreadProcessId(window, out var id);
            using var process = Process.GetProcessById((int)id);
            return Matches(process.ProcessName, entries);
        }
        catch { return true; } // 配置了排除规则却无法确认来源时，自动取词宁可不执行。
    }
    internal static bool ForegroundExcluded => IsExcluded(Win32.GetForegroundWindow());
}
