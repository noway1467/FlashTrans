using FlashTrans.Core;
using FlashTrans.Services;

namespace FlashTrans.Interop;

public enum HotkeyAction
{
    TranslateSelection, ToggleWindow, TranslateClipboard, ToggleSelection, NextProvider, CaptureOcr
}

/// <summary>全局热键注册。冲突时返回失败原因，交给界面提示。</summary>
public sealed class HotkeyManager(IntPtr hwnd) : IDisposable
{
    readonly Dictionary<int, HotkeyAction> _ids = [];
    readonly List<string> _failures = [];
    int _next = 0xA100;

    public event Action<HotkeyAction>? Triggered;

    /// <summary>上次注册中失败的项（例如被别的程序占用）。</summary>
    public IReadOnlyList<string> Failures => _failures;

    public void Rebind(AppSettings s)
    {
        UnbindAll();
        Bind(HotkeyAction.TranslateSelection, s.HkTranslateSelection, "翻译选中文本");
        Bind(HotkeyAction.ToggleWindow, s.HkToggleWindow, "显示/隐藏主窗口");
        Bind(HotkeyAction.TranslateClipboard, s.HkTranslateClipboard, "翻译剪贴板");
        Bind(HotkeyAction.ToggleSelection, s.HkToggleSelection, "开关划词翻译");
        Bind(HotkeyAction.NextProvider, s.HkNextProvider, "切换下一个翻译源");
        Bind(HotkeyAction.CaptureOcr, s.HkCaptureOcr, "截图");
    }

    void Bind(HotkeyAction action, string? text, string label)
    {
        var spec = HotkeySpec.Parse(text);
        if (spec.IsEmpty) return;

        var id = _next++;
        if (Win32.RegisterHotKey(hwnd, id, spec.Win32Modifiers, spec.VirtualKey))
            _ids[id] = action;
        else
            _failures.Add($"{label}（{spec}）注册失败，可能已被其它程序占用");
    }

    public void UnbindAll()
    {
        foreach (var id in _ids.Keys) Win32.UnregisterHotKey(hwnd, id);
        _ids.Clear();
        _failures.Clear();
    }

    /// <summary>由消息窗口转发 WM_HOTKEY。</summary>
    public bool Handle(IntPtr wParam)
    {
        if (!_ids.TryGetValue(wParam.ToInt32(), out var action)) return false;
        try { Triggered?.Invoke(action); }
        catch (Exception ex) { Log.Error("热键处理失败", ex); }
        return true;
    }

    public void Dispose() => UnbindAll();
}
