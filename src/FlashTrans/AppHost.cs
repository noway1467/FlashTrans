using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans;

/// <summary>全局协调者：消息窗口、托盘、热键、划词、窗口生命周期。</summary>
public sealed partial class AppHost : IDisposable
{
    MessageWindow _msg = null!;
    TrayIcon _tray = null!;
    HotkeyManager _hotkeys = null!;
    readonly MouseSelectionHook _mouseHook = new();
    readonly DoubleCtrlHook _ctrlHook = new();

    MainWindow? _main;
    PopupWindow? _popup;
    SelectionIcon? _icon;
    SettingsWindow? _settings;

    uint _lastClipSeq;
    bool _clipboardHooked;
    CancellationTokenSource? _selectionCts;

    static AppSettings S => SettingsService.Instance.Current;

    public void Start(bool startHidden)
    {
        _msg = new MessageWindow("FlashTrans.Messages");
        _msg.Message += OnMessage;

        _tray = new TrayIcon(_msg.Handle, TrayTip());
        _tray.LeftClick += () => ToggleMainWindow();
        _tray.DoubleClick += () => ShowMainWindow(focusInput: true);
        _tray.RightClick += ShowTrayMenu;

        _hotkeys = new HotkeyManager(_msg.Handle);
        _hotkeys.Triggered += OnHotkey;
        _hotkeys.Rebind(S);
        ReportHotkeyFailures();

        SettingsService.Instance.Changed += OnSettingsChanged;
        ApplyInputHooks(S);

        if (!startHidden) ShowMainWindow(focusInput: true);
        WarmupWhenIdle();
        if (startHidden) PreloadWhenIdle();
    }

    // ------------------------------------------------------------- 消息分发

    bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_HOTKEY:
                return _hotkeys.Handle(wParam);
            case Win32.WM_TRAY:
                _tray.Handle(lParam);
                return true;
            case Win32.WM_CLIPBOARDUPDATE:
                OnClipboardUpdate();
                return true;
        }
        return false;
    }

    void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.TranslateSelection:
                _ = TranslateSelectionAsync(fromHotkey: true);
                break;
            case HotkeyAction.ToggleWindow:
                ToggleMainWindow();
                break;
            case HotkeyAction.TranslateClipboard:
                var text = SelectionReader.ReadText();
                if (!string.IsNullOrWhiteSpace(text)) ShowPopupFor(text!, null);
                else Toast("剪贴板里没有文本");
                break;
            case HotkeyAction.ToggleSelection:
                ToggleSelectionMode();
                break;
            case HotkeyAction.NextProvider:
                ShowMainWindow(focusInput: false);
                _main?.SelectNextProvider();
                break;
            case HotkeyAction.CaptureOcr:
                _ = CaptureAsync();
                break;
        }
    }

    // ------------------------------------------------------------- 划词

    void ApplyInputHooks(AppSettings s)
    {
        _mouseHook.RequiredModifier = s.SelectionModifier;
        _mouseHook.ShouldIgnore = () => s.SkipOwnWindow && IsOwnWindowActive();

        if (s.SelectionMode == SelectionMode.Off)
        {
            _mouseHook.Stop();
            _mouseHook.SelectionMade -= OnSelectionMade;
            HideSelectionIcon();   // 关掉时把已经弹出来的图标一起收走
        }
        else if (!_mouseHook.IsRunning)
        {
            _mouseHook.SelectionMade -= OnSelectionMade;
            _mouseHook.SelectionMade += OnSelectionMade;
            _mouseHook.Start();
        }

        if (s.DoubleCtrlWake)
        {
            if (!_ctrlHook.IsRunning)
            {
                _ctrlHook.Triggered -= OnDoubleCtrl;
                _ctrlHook.Triggered += OnDoubleCtrl;
                _ctrlHook.Start();
            }
        }
        else
        {
            _ctrlHook.Stop();
            _ctrlHook.Triggered -= OnDoubleCtrl;
        }

        HookClipboard(s.MonitorClipboard);
    }

    void OnDoubleCtrl() => Application.Current?.Dispatcher.BeginInvoke(
        () => _ = TranslateSelectionAsync(fromHotkey: true), DispatcherPriority.Normal);

    void OnSelectionMade(POINT pt)
    {
        // 钩子线程回到界面线程
        Application.Current?.Dispatcher.BeginInvoke(() => _ = HandleSelectionAsync(pt),
            DispatcherPriority.Background);
    }

    async Task HandleSelectionAsync(POINT pt)
    {
        var mode = S.SelectionMode;
        if (mode == SelectionMode.Off) return;

        _selectionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _selectionCts = cts;

        try
        {
            await Task.Delay(60, cts.Token);   // 等目标程序把选区确定下来
            var text = await SelectionReader.GetSelectedTextAsync(S.RestoreClipboard, cts.Token);
            if (cts.IsCancellationRequested || string.IsNullOrWhiteSpace(text)) return;
            if (text!.Length > S.MaxSelectionChars) return;

            var anchor = ScreenHelper.ToDip(pt, _popup);
            if (mode == SelectionMode.Direct) ShowPopupFor(text, anchor);
            else ShowSelectionIcon(text, anchor);
        }
        catch (OperationCanceledException) { /* 新的划词覆盖了旧的 */ }
        catch (Exception ex) { Log.Warn("划词处理失败：" + ex.Message); }
    }

    async Task TranslateSelectionAsync(bool fromHotkey)
    {
        var text = await SelectionReader.GetSelectedTextAsync(S.RestoreClipboard);
        if (string.IsNullOrWhiteSpace(text))
        {
            if (fromHotkey) Toast("没有选中文本");
            return;
        }
        Point? anchor = null;
        if (S.PopupPlace == PopupPlace.NearMouse)
            anchor = ScreenHelper.ToDip(ScreenHelper.CursorPos(), _popup);
        ShowPopupFor(text!, anchor);
    }

    void ToggleSelectionMode()
    {
        S.SelectionMode = S.SelectionMode == SelectionMode.Off ? SelectionMode.Icon : SelectionMode.Off;
        SettingsService.Instance.Touch();
        Toast(S.SelectionMode == SelectionMode.Off ? "划词翻译已关闭" : "划词翻译已开启");
    }

    // ------------------------------------------------------------- 剪贴板监听

    void HookClipboard(bool enable)
    {
        if (enable == _clipboardHooked) return;
        if (enable)
        {
            _clipboardHooked = Win32.AddClipboardFormatListener(_msg.Handle);
            _lastClipSeq = Win32.GetClipboardSequenceNumber();
        }
        else
        {
            Win32.RemoveClipboardFormatListener(_msg.Handle);
            _clipboardHooked = false;
        }
    }

    void OnClipboardUpdate()
    {
        if (!S.MonitorClipboard) return;
        var seq = Win32.GetClipboardSequenceNumber();
        if (seq == _lastClipSeq) return;
        _lastClipSeq = seq;
        if (S.SkipOwnWindow && IsOwnWindowActive()) return;

        var text = SelectionReader.ReadText();
        if (string.IsNullOrWhiteSpace(text) || text!.Length > S.MaxSelectionChars) return;
        ShowPopupFor(text, null);
    }

    // ------------------------------------------------------------- 工具

    static bool IsOwnWindowActive()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }

    static string TrayTip()
    {
        var hk = HotkeySpec.Parse(S.HkTranslateSelection);
        return hk.IsEmpty ? "闪译 FlashTrans" : $"闪译 FlashTrans（{hk} 翻译选中文本）";
    }

    public void Toast(string message, Action? onClick = null)
    {
        // 自绘的右下角轻提示，不走系统气泡：气泡会被专注助手静默吞掉。
        ToastWindow.Show(message, onClick);
    }

    void ReportHotkeyFailures()
    {
        if (_hotkeys.Failures.Count > 0)
            Toast(string.Join("；", _hotkeys.Failures));
    }

    void OnSettingsChanged(AppSettings s)
    {
        ThemeService.Apply(s);
        _hotkeys.Rebind(s);
        ReportHotkeyFailures();
        ApplyInputHooks(s);
        _tray.UpdateTip(TrayTip());
        _main?.OnSettingsChanged();
        _popup?.OnSettingsChanged();
    }

    /// <summary>
    /// 预热挪到界面空下来之后。它要占线程池线程去做 DNS + TLS，
    /// 原来卡在 Start() 里和托盘、热键、窗口构造抢资源，把启动拖慢了。
    /// </summary>
    void WarmupWhenIdle()
    {
        if (!S.WarmupOnStart) return;
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { ProviderRegistry.WarmupFor(S.Providers); }
            catch (Exception ex) { Log.Warn("预热失败：" + ex.Message); }
        };
        timer.Start();
    }

    /// <summary>空闲时预先构造窗口，首次唤出时几乎无延迟。</summary>
    void PreloadWhenIdle()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                EnsureMain();
                EnsurePopup();
            }
            catch (Exception ex) { Log.Warn("预加载窗口失败：" + ex.Message); }
        };
        timer.Start();
    }

    public void Dispose()
    {
        SettingsService.Instance.Changed -= OnSettingsChanged;
        _selectionCts?.Cancel();
        _mouseHook.Dispose();
        _ctrlHook.Dispose();
        _hotkeys?.Dispose();
        HookClipboard(false);
        _tray?.Dispose();
        _icon?.Close();
        _popup?.Close();
        ToastWindow.Shutdown();
        _main?.PersistGeometry();
        SettingsService.Instance.Save();
        TranslateEngine.Instance.Cache.Dispose();
        _msg?.Dispose();
    }
}
