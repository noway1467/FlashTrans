using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace FlashTrans.Services;

public static partial class StartupService
{
    const string TaskSource = "FlashTrans.Startup.v1";
    static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    internal static string TaskName(string sid) => "FlashTrans.Startup." + sid;

    internal static string BuildTaskXml(string exe, string sid)
    {
        var ns = TaskNamespace;
        return new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo",
                new XElement(ns + "Description", "FlashTrans 管理员权限自启动；可在程序设置 → 通用关闭。"),
                new XElement(ns + "Source", TaskSource)),
            new XElement(ns + "Triggers", new XElement(ns + "LogonTrigger",
                new XElement(ns + "Enabled", true), new XElement(ns + "UserId", sid))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"),
                new XElement(ns + "UserId", sid), new XElement(ns + "LogonType", "InteractiveToken"),
                new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", false),
                new XElement(ns + "StopIfGoingOnBatteries", false),
                new XElement(ns + "StartWhenAvailable", true),
                new XElement(ns + "RunOnlyIfNetworkAvailable", false),
                new XElement(ns + "AllowStartOnDemand", true),
                new XElement(ns + "Enabled", true),
                new XElement(ns + "RunOnlyIfIdle", false),
                new XElement(ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"),
                new XElement(ns + "Exec", new XElement(ns + "Command", exe),
                    new XElement(ns + "Arguments", TrayArg),
                    new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(exe)))))
            .ToString();
    }

    internal static bool TaskOwnedBy(string xml, string sid)
    {
        var root = XElement.Parse(xml);
        return (string?)root.Element(TaskNamespace + "RegistrationInfo")?.Element(TaskNamespace + "Source") == TaskSource
            && TaskUserMatches((string?)root.Element(TaskNamespace + "Principals")?.Element(TaskNamespace + "Principal")?
                .Element(TaskNamespace + "UserId"), sid);
    }

    internal static bool TaskUserMatches(string? userId, string sid)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        if (string.Equals(userId, sid, StringComparison.OrdinalIgnoreCase)) return true;
        // Task Scheduler 回读时可能把登录触发器中的 SID 改写为 DOMAIN\User。
        // 用 Windows 解析到同一 SID，不能按字符串判错，也不能只比较用户名而忽略域。
        try { return new NTAccount(userId).Translate(typeof(SecurityIdentifier)).Value == sid; }
        catch (IdentityNotMappedException) { return false; }
        catch (ArgumentException) { return false; }
    }

    internal static bool TaskAligned(string? xml, string exe, string sid)
    {
        if (xml is null || !TaskOwnedBy(xml, sid)) return false;
        var root = XElement.Parse(xml);
        var ns = TaskNamespace;
        var principal = root.Element(ns + "Principals")?.Element(ns + "Principal");
        var triggers = root.Element(ns + "Triggers")?.Elements().ToArray();
        var actions = root.Element(ns + "Actions")?.Elements().ToArray();
        var settings = root.Element(ns + "Settings");
        return (string?)principal?.Element(ns + "LogonType") == "InteractiveToken"
            && (string?)principal?.Element(ns + "RunLevel") == "HighestAvailable"
            && triggers is { Length: 1 } && triggers[0].Name == ns + "LogonTrigger"
            && TaskUserMatches((string?)triggers[0].Element(ns + "UserId"), sid)
            && (bool?)triggers[0].Element(ns + "Enabled") != false
            && actions is { Length: 1 } && actions[0].Name == ns + "Exec"
            && SamePath((string?)actions[0].Element(ns + "Command"), exe)
            && (string?)actions[0].Element(ns + "Arguments") == TrayArg
            && SamePath((string?)actions[0].Element(ns + "WorkingDirectory"), Path.GetDirectoryName(exe))
            && (bool?)settings?.Element(ns + "Enabled") != false
            && (bool?)settings?.Element(ns + "DisallowStartIfOnBatteries") == false
            && (bool?)settings?.Element(ns + "StopIfGoingOnBatteries") == false
            && (bool?)settings?.Element(ns + "RunOnlyIfIdle") != true
            && (bool?)settings?.Element(ns + "RunOnlyIfNetworkAvailable") != true
            && (string?)settings?.Element(ns + "ExecutionTimeLimit") == "PT0S"
            && (string?)settings?.Element(ns + "MultipleInstancesPolicy") == "IgnoreNew";
    }
}

/// <summary>Windows 自带的 Task Scheduler COM 接口，不需要随程序分发额外组件。</summary>
internal sealed class StartupTaskStore : IDisposable
{
    readonly dynamic _service;
    readonly dynamic _folder;

    internal StartupTaskStore()
    {
        _service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        try
        {
            _service.Connect();
            _folder = _service.GetFolder(@"\");
        }
        catch { Marshal.ReleaseComObject(_service); throw; }
    }

    internal string? Read(string name)
    {
        dynamic task;
        try { task = _folder.GetTask(name); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return null; }
        try { return (string)task.Xml; }
        finally { Marshal.ReleaseComObject(task); }
    }

    internal void Register(string name, string xml)
    {
        // 6 = 创建或更新，3 = 当前已登录用户；不提供密码，也不使用 SYSTEM 账号。
        object task = _folder.RegisterTask(name, xml, 6, null, null, 3, null);
        Marshal.ReleaseComObject(task);
    }

    internal void Delete(string name) => _folder.DeleteTask(name, 0);

    public void Dispose()
    {
        Marshal.ReleaseComObject(_folder);
        Marshal.ReleaseComObject(_service);
    }
}
