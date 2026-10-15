using FlashTrans.Core;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.Interop;

public enum HotkeyAction
{
    TranslateSelection, ToggleWindow, TranslateClipboard, ToggleSelection, NextProvider, CaptureOcr,
    TogglePopup, QuickLaunch
}

/// <summary>全局热键注册。新键注册成功才释放旧键，避免冲突后连原入口也丢失。</summary>
public sealed class HotkeyManager(IntPtr hwnd) : IDisposable
{
    readonly Dictionary<HotkeyAction, (int Id, HotkeySpec Spec)> _bindings = [];
    readonly List<string> _failures = [];
    int _next = 0xA100;

    public event Action<HotkeyAction>? Triggered;
    public IReadOnlyList<string> Failures => _failures;

    internal HotkeySpec? GetBinding(HotkeyAction action) => _bindings.TryGetValue(action, out var binding) ? binding.Spec : null;

    internal static IEnumerable<(HotkeyAction Action, string Text, string Label)> Definitions(AppSettings s)
    {
        yield return (HotkeyAction.TranslateSelection, s.HkTranslateSelection, "翻译选中文本");
        yield return (HotkeyAction.ToggleWindow, s.HkToggleWindow, "显示/隐藏主窗口");
        yield return (HotkeyAction.TranslateClipboard, s.HkTranslateClipboard, "翻译剪贴板");
        yield return (HotkeyAction.ToggleSelection, s.HkToggleSelection, "开关划词翻译");
        yield return (HotkeyAction.NextProvider, s.HkNextProvider, "切换下一个翻译源");
        yield return (HotkeyAction.CaptureOcr, s.HkCaptureOcr, "截图");
        yield return (HotkeyAction.TogglePopup, s.HkTogglePopup, "收起/叫回翻译弹窗");
        yield return (HotkeyAction.QuickLaunch, s.HkQuickLaunch, "快速启动");
    }

    public static string? FindConflict(AppSettings s, HotkeyAction action, string? text)
    {
        var spec = HotkeySpec.Parse(text);
        if (spec.IsEmpty) return null;
        foreach (var binding in Definitions(s))
            if (binding.Action != action && HotkeySpec.Parse(binding.Text) == spec)
                return $"与「{binding.Label}」的快捷键 {spec} 重复";
        if (spec == HotkeySpec.Parse(RecordHud.PauseHotkey) || spec == HotkeySpec.Parse(RecordHud.CancelHotkey))
            return $"{spec} 已用于录制暂停或取消";

        // 启动面板不能抢掉截图工具和翻译窗口的操作键。
        if (action == HotkeyAction.QuickLaunch)
        {
            string[] local = [s.CkRect, s.CkEllipse, s.CkArrow, s.CkPen, s.CkMosaic,
                s.CkRedact, s.CkAutoRedact, s.CkText, s.CkUndo, s.CkRedo, s.CkCopy,
                s.CkSave, s.CkOcr, s.CkOcrTranslate, s.CkPin, s.CkLongShot, s.CkRecord,
                "Ctrl+Enter", "Ctrl+Tab", "Ctrl+Shift+Tab", "Ctrl+Shift+S", "Ctrl+D", "Ctrl+L", "Ctrl+OemComma",
                "Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y",
                "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5", "Ctrl+6", "Ctrl+7", "Ctrl+8", "Ctrl+9"];
            if (local.Any(k => HotkeySpec.Parse(k) == spec)) return $"{spec} 已用于窗口或截图操作";
        }
        return null;
    }

    public void Rebind(AppSettings s)
    {
        _failures.Clear();
        var seen = new HashSet<HotkeySpec>();
        var requests = new List<(HotkeyAction Action, HotkeySpec Spec, string Label, string? Conflict)>();
        foreach (var (action, text, label) in Definitions(s))
        {
            var spec = HotkeySpec.Parse(text);
            // 旧配置已有重复项时，按原顺序保留先注册的功能；新入口绝不抢旧功能。
            var conflict = !spec.IsEmpty && !seen.Add(spec) ? "与其他全局快捷键重复" : null;
            if (action == HotkeyAction.QuickLaunch) conflict ??= FindConflict(s, action, text);
            requests.Add((action, spec, label, conflict));
        }
        // 重置/导入可能把键位在功能间交换。先释放确实要移交的旧键，避免注册顺序
        // 导致双方互相阻挡；没有移交的键仍保留到新键注册成功为止。
        foreach (var (action, binding) in _bindings.ToArray())
            if (requests.Any(r => r.Conflict is null && r.Action != action && r.Spec == binding.Spec))
                Remove(action);
        foreach (var (action, spec, label, conflict) in requests)
        {
            if (conflict is not null)
            {
                Remove(action);
                _failures.Add($"{label}（{spec}）未启用：{conflict}，请在设置中更换");
            }
            else if (!TryBind(action, spec, out var error)) _failures.Add($"{label}：{error}");
        }
    }

    public bool TryChange(AppSettings s, HotkeyAction action, string text, out string? error)
    {
        error = FindConflict(s, action, text);
        return error is null && TryBind(action, HotkeySpec.Parse(text), out error);
    }

    bool TryBind(HotkeyAction action, HotkeySpec spec, out string? error)
    {
        error = null;
        if (spec.IsEmpty) { Remove(action); return true; }
        if (_bindings.TryGetValue(action, out var previous) && previous.Spec == spec) return true;
        if (_bindings.Any(b => b.Key != action && b.Value.Spec == spec))
        {
            error = $"{spec} 已用于其他功能，原快捷键保持不变";
            return false;
        }
        // 不再每次设置保存都递增/重注册，且循环使用合法的应用热键 ID 区间。
        int id;
        do { id = _next++; if (_next >= 0xB000) _next = 0xA100; }
        while (_bindings.Values.Any(b => b.Id == id));
        if (!Win32.RegisterHotKey(hwnd, id, spec.Win32Modifiers, spec.VirtualKey))
        {
            error = $"{spec} 注册失败，可能被其他程序或系统占用；请更换，原快捷键保持不变";
            return false;
        }
        Remove(action);
        _bindings[action] = (id, spec);
        return true;
    }

    void Remove(HotkeyAction action)
    {
        if (_bindings.Remove(action, out var binding)) Win32.UnregisterHotKey(hwnd, binding.Id);
    }

    public void UnbindAll()
    {
        foreach (var binding in _bindings.Values) Win32.UnregisterHotKey(hwnd, binding.Id);
        _bindings.Clear();
        _failures.Clear();
    }

    public bool Handle(IntPtr wParam)
    {
        foreach (var (action, binding) in _bindings)
        {
            if (binding.Id != wParam.ToInt32()) continue;
            try { Triggered?.Invoke(action); }
            catch (Exception ex) { Log.Error("热键处理失败", ex); }
            return true;
        }
        return false;
    }

    public void Dispose() => UnbindAll();
}
