using System.Diagnostics;
using Microsoft.Win32;

namespace FlashTrans.Services;

/// <summary>开机自启（写当前用户的 Run 项，不需要管理员权限）。</summary>
public static class StartupService
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "FlashTrans";

    static string ExePath =>
        Process.GetCurrentProcess().MainModule?.FileName
        ?? System.IO.Path.Combine(AppContext.BaseDirectory, "FlashTrans.exe");

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Contains("FlashTrans", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;

            if (enabled) key.SetValue(ValueName, $"\"{ExePath}\" --tray");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("设置开机自启失败：" + ex.Message);
            return false;
        }
    }
}
