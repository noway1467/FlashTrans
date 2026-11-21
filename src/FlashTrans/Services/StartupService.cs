using System.IO;
using System.Reflection;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace FlashTrans.Services;

/// <summary>
/// 普通自启使用当前用户的 Run 项，管理员自启使用同一用户的登录任务。
/// 后台同步绝不弹 UAC；只有用户操作设置时才启动提权助手。
/// </summary>
public static partial class StartupService
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "FlashTrans";
    const string ExeName = "FlashTrans";
    internal const string ConfigureArgument = "--configure-startup";

    public static string? LastError { get; private set; }
    static bool _changing;
    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal static string CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new InvalidOperationException("无法确定当前 Windows 账号。");
        }
    }

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

    static bool CanConfigure => RunningAsApp && !Environment.GetCommandLineArgs().Contains("--benchmark");

    /// <summary>启动和导入配置时静默对齐；需要授权时返回失败，交给设置页明确处理。</summary>
    public static bool Sync(bool want, bool administrator = false)
    {
        // 用户正在授权时，关闭设置窗口或修改外观也会触发保存；不能用旧值抢先覆盖新任务。
        if (_changing) return true;
        LastError = null;
        if (!CanConfigure)
        {
            LastError = "诊断或自测进程不能修改开机自启动。";
            return false;
        }
        try { Configure(want, administrator); return true; }
        catch (Exception ex)
        {
            LastError = NeedsElevation(ex)
                ? "启动方式需要管理员授权，请到设置 → 通用点击「重新应用启动设置」。"
                : "设置自启动失败：" + ex.Message;
            Log.Warn(LastError);
            return false;
        }
    }

    /// <summary>用户主动改开关时允许一次系统授权；取消或失败不修改内存中的设置。</summary>
    public static async Task<string?> ChangeAsync(bool want, bool administrator)
    {
        if (!CanConfigure) return "诊断或自测进程不能修改开机自启动。";
        if (_changing) return "正在应用启动设置，请稍候。";
        _changing = true;
        try
        {
            try { await Task.Run(() => Configure(want, administrator)); }
            catch (Exception ex) when (NeedsElevation(ex) && !IsAdministrator)
            {
                var start = new ProcessStartInfo(ExePath) { UseShellExecute = true, Verb = "runas" };
                start.ArgumentList.Add(ConfigureArgument);
                start.ArgumentList.Add(want ? administrator ? "admin" : "normal" : "off");
                start.ArgumentList.Add(CurrentUserSid);
                using var helper = Process.Start(start) ?? throw new InvalidOperationException("无法启动授权助手。");
                await helper.WaitForExitAsync();
                if (helper.ExitCode != 0)
                    throw new InvalidOperationException(helper.ExitCode == 2
                        ? "请使用当前 Windows 账号授权，不能改用其他管理员账号。"
                        : "管理员启动任务设置失败，请检查任务计划程序服务或安全软件的拦截提示。");
            }
            if (!IsConfigured(want, administrator))
                throw new InvalidOperationException("启动设置未通过核验，请重新应用。");
            LastError = null;
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return LastError = "已取消管理员授权，启动设置保持原样。";
        }
        catch (Exception ex)
        {
            Log.Warn("更改启动方式失败：" + ex.Message);
            return LastError = "设置失败：" + ex.Message;
        }
        finally { _changing = false; }
    }

    /// <summary>必须在单实例检查前执行；助手只改启动登记，不加载或保存用户配置。</summary>
    internal static int? HandleConfigurationCommand(string[] args)
    {
        if (args.Length == 0 || args[0] != ConfigureArgument) return null;
        if (!CanConfigure || args.Length != 3 || args[1] is not ("admin" or "normal" or "off")
            || args[2] != CurrentUserSid) return 2;
        if (!IsAdministrator) return 1;
        return Sync(args[1] != "off", args[1] == "admin") ? 0 : 1;
    }

    static bool NeedsElevation(Exception ex) => ex is UnauthorizedAccessException
        || ex is COMException { HResult: unchecked((int)0x80070005) };

    public static bool IsConfigured(bool want, bool administrator)
    {
        try
        {
            using var tasks = new StartupTaskStore();
            var xml = tasks.Read(TaskName(CurrentUserSid));
            var command = ReadRunCommand();
            return want && administrator
                ? command is null && TaskAligned(xml, ExePath, CurrentUserSid)
                : xml is null && (want ? Aligned(command, ExePath) : command is null);
        }
        catch (Exception ex) { Log.Warn("核验自启动失败：" + ex.Message); return false; }
    }

    static void Configure(bool want, bool administrator)
    {
        var exe = ExePath;
        if (want && !File.Exists(exe)) throw new FileNotFoundException("找不到当前程序，未修改启动项。", exe);
        var sid = CurrentUserSid;
        var name = TaskName(sid);
        using var tasks = new StartupTaskStore();
        var previousTask = tasks.Read(name);
        if (previousTask is not null && !TaskOwnedBy(previousTask, sid))
            throw new InvalidOperationException("同名计划任务不属于 FlashTrans，未覆盖；请检查任务计划程序。");
        var previousCommand = ReadRunCommand();
        var elevated = want && administrator;
        var changeTask = elevated ? !TaskAligned(previousTask, exe, sid) : previousTask is not null;
        if (changeTask && !IsAdministrator) throw new UnauthorizedAccessException("需要管理员授权才能更改登录任务。");

        if (changeTask)
        {
            if (elevated) tasks.Register(name, BuildTaskXml(exe, sid));
            else tasks.Delete(name);
        }
        try
        {
            // 两种自启互斥；先让目标任务落地，再移除普通启动项，避免普通实例抢先占位。
            var desiredCommand = want && !administrator ? CommandFor(exe) : null;
            if (previousCommand != desiredCommand) WriteRunCommand(desiredCommand);
        }
        catch (Exception failure)
        {
            if (changeTask)
            {
                try
                {
                    if (previousTask is null) tasks.Delete(name);
                    else tasks.Register(name, previousTask);
                }
                catch (Exception rollback)
                {
                    throw new InvalidOperationException("启动项写入失败，恢复计划任务也失败；请重新应用启动设置。",
                        new AggregateException(failure, rollback));
                }
            }
            throw;
        }
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

    static string? ReadRunCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    static void WriteRunCommand(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new IOException("无法打开当前用户的启动项。");
        if (command is not null) key.SetValue(ValueName, command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
