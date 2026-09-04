using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace FlashTrans.Services;

/// <summary>
/// 开机自启（写当前用户的 Run 项，不需要管理员权限）。
///
/// Run 项里存的是**绝对路径**，而这是个绿色包：换个目录、升级换成带版本号的文件夹，
/// 那条命令就指向一个不存在的 exe —— 开机什么都不会启动，设置里却还勾着。
/// 所以开关的真实状态存在 settings.json 的 RunAtStartup 里，注册表只是它的投影，
/// 每次启动对齐一次（AppHost.SyncStartupWhenIdle），路径变了就改写。
/// </summary>
public static class StartupService
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "FlashTrans";
    const string ExeName = "FlashTrans";

    /// <summary>静默启动到托盘，不弹主窗口。</summary>
    public const string TrayArg = "--tray";

    /// <summary>该写进注册表的那个 exe。</summary>
    public static string ExePath
    {
        get
        {
            // 单文件发布下 ProcessPath 给的就是宿主 exe 自己，正是要写的那个（用户把它改了名也照样对）；
            // AppContext.BaseDirectory 那时指向解包出来的临时目录，拼出来的路径下次启动就没了。
            var p = Environment.ProcessPath;
            // dotnet run 起来时 ProcessPath 是 dotnet.exe，写它等于开机启动一个空的 dotnet
            var viaDotnet = p is null ||
                string.Equals(Path.GetFileNameWithoutExtension(p), "dotnet", StringComparison.OrdinalIgnoreCase);
            return viaDotnet ? Path.Combine(AppContext.BaseDirectory, ExeName + ".exe") : p!;
        }
    }

    /// <summary>
    /// 跑的是本程序自己，而不是别的程序（自测套件）加载了这个程序集。
    /// 按程序集名判断而不是按 exe 文件名：用户把 exe 改个名也该照样能设开机自启。
    /// </summary>
    static bool RunningAsApp =>
        string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, ExeName, StringComparison.OrdinalIgnoreCase);

    static string CommandFor(string exePath) => $"\"{exePath}\" {TrayArg}";

    /// <summary>注册表里现在写着的那条命令，没有这个值就是 null。</summary>
    public static string? CurrentCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex)
        {
            Log.Warn("读开机自启项失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>Run 项里有本程序的条目（不管它指向哪个 exe）。</summary>
    public static bool IsRegistered() => CurrentCommand() is not null;

    /// <summary>
    /// 注册表里那条命令指的是不是当前这个 exe，并且带着 --tray。
    /// 路径漂了就是 false —— 那种条目开机启不起来任何东西。
    /// </summary>
    public static bool IsEnabled() => Aligned(CurrentCommand(), ExePath);

    /// <summary>
    /// 把注册表对齐到 want：该有的写上（顺带把旧路径改写成当前 exe），不该有的删掉。
    /// 已经一致就一个字节都不动。返回是否落地成功。
    /// </summary>
    public static bool Sync(bool want)
    {
        // 自测程序的 bin 目录里也有一份 FlashTrans.exe。真让它写进 Run 项，
        // 用户的开机自启就被指到测试输出目录去了，所以不是本程序在跑就不碰注册表。
        if (!RunningAsApp) return false;

        var current = CurrentCommand();
        var exe = ExePath;

        if (want && Aligned(current, exe)) return true;
        if (!want && current is null) return true;

        if (want && !File.Exists(exe))
        {
            // 宁可不写，也别在 Run 里留一条指向不存在文件的命令 —— 那正是要修的毛病
            Log.Warn($"找不到 {exe}，没写开机自启项");
            return false;
        }

        var ok = Write(want, CommandFor(exe));
        if (ok && want && current is not null)
            Log.Warn($"开机自启的路径变了，已改写：{PathOf(current)} → {exe}");
        return ok;
    }

    /// <summary>那条命令是否已经指向 exePath，且带着 --tray。</summary>
    internal static bool Aligned(string? command, string exePath)
        => command is not null
           && SamePath(PathOf(command), exePath)
           && command.Contains(TrayArg, StringComparison.OrdinalIgnoreCase);

    /// <summary>从 Run 项的命令行里取出 exe 路径：带引号的脱引号，不带引号的截到第一个空格。</summary>
    internal static string? PathOf(string? command)
    {
        var s = command?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        var space = s.IndexOf(' ');
        return space < 0 ? s : s[..space];
    }

    /// <summary>两个路径指向同一个文件。大小写、`..`、结尾反斜杠都不算区别。</summary>
    internal static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 命令行里存的东西不保证是合法路径（含通配符、非法字符），退回字面比较
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    static bool Write(bool enabled, string command)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;

            if (enabled) key.SetValue(ValueName, command, RegistryValueKind.String);
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
