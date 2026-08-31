using System.IO;
using System.Text;

namespace FlashTrans.Services;

/// <summary>轻量日志：仅在出错时写入，避免影响启动速度。</summary>
public static class Log
{
    static readonly object Gate = new();
    static string? _path;

    public static string Path => _path ??=
        System.IO.Path.Combine(SettingsService.Instance.ConfigDir, "flashtrans.log");

    static string Path0 => Path;

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsService.Instance.ConfigDir);
                var fi = new FileInfo(Path0);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(Path0,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { /* 日志失败不能影响主流程 */ }
    }
}
