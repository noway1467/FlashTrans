using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;
using SelectionMode = FlashTrans.Core.SelectionMode;

namespace FlashTrans;

public sealed partial class AppHost
{
    // ------------------------------------------------------------- 主窗口

    MainWindow EnsureMain()
    {
        if (_main is not null) return _main;
        _main = new MainWindow(this);
        _main.Closing += (_, e) =>
        {
            if (!_shuttingDown && S.CloseToTray)
            {
                e.Cancel = true;
                _main!.PersistGeometry();
                _main.Hide();
            }
        };
        return _main;
    }

    bool _shuttingDown;

    public void ShowMainWindow(bool focusInput, string? text = null)
    {
        var w = EnsureMain();
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = S.AlwaysOnTop || true;      // 先置顶抢焦点
        w.Topmost = S.AlwaysOnTop;

        if (text is not null) w.SetInput(text, translate: true);
        else if (focusInput) w.FocusInput();
    }

    public void ToggleMainWindow()
    {
        if (_main is { IsVisible: true } w && IsOwnWindowActive())
        {
            w.PersistGeometry();
            w.Hide();
        }
        else ShowMainWindow(focusInput: true);
    }

    /// <summary>把弹窗里的内容接管到主窗口（「展开」按钮）。</summary>
    public void ExpandToMain(string text)
    {
        _popup?.ClosePopup();
        ShowMainWindow(focusInput: false, text: text);
    }

    // ------------------------------------------------------------- 弹窗

    PopupWindow EnsurePopup() => _popup ??= new PopupWindow(this);

    public void ShowPopupFor(string text, Point? anchor)
    {
        HideSelectionIcon();
        var p = EnsurePopup();
        p.ShowFor(text, anchor);
    }

    /// <summary>
    /// 收起 / 叫回翻译弹窗。开着就收起（内容留着），收起了就原样放回来。
    /// 被 Esc 或关闭按钮关掉的不算，那是用户不要了。
    /// </summary>
    public void TogglePopupWindow()
    {
        if (_popup is null) { Toast("现在没有翻译弹窗"); return; }
        if (_popup.IsVisible)
        {
            // 弹窗不再永久置顶，可能正被别的窗口盖着。这种情况先把它抬上来，
            // 别一按就收——盖住的时候用户想要的是「让我看见」。
            if (!IsWindowForeground(_popup)) _popup.RaiseToFront();
            else _popup.StashPopup();
            return;
        }
        if (_popup.RestorePopup()) return;
        Toast("没有收起的翻译弹窗可以叫回");
    }

    static bool IsWindowForeground(Window w)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        return hwnd != IntPtr.Zero && hwnd == Win32.GetForegroundWindow();
    }

    // ------------------------------------------------------------- 划词图标

    void ShowSelectionIcon(string text, Point anchor)
    {
        _icon ??= new SelectionIcon();
        _icon.Clicked = t => ShowPopupFor(t, anchor);
        _icon.ShowAt(anchor, text);
    }

    void HideSelectionIcon() => _icon?.HideIcon();

    // ------------------------------------------------------------- 设置窗口

    public void ShowSettings(string? tab = null)
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            if (tab is not null) _settings.SelectTab(tab);
            return;
        }
        _settings = new SettingsWindow(this);
        _settings.Closed += (_, _) => _settings = null;
        if (tab is not null) _settings.SelectTab(tab);
        _settings.Show();
        _settings.Activate();
    }

    // ------------------------------------------------------------- 托盘菜单

    ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(Item("显示主窗口", HotkeySpec.Parse(S.HkToggleWindow).ToString(),
            () => ShowMainWindow(focusInput: true)));
        menu.Items.Add(Item("翻译选中文本", HotkeySpec.Parse(S.HkTranslateSelection).ToString(),
            () => _ = TranslateSelectionAsync(fromHotkey: true)));
        menu.Items.Add(Item("翻译剪贴板", HotkeySpec.Parse(S.HkTranslateClipboard).ToString(), () =>
        {
            var t = SelectionReader.ReadText();
            if (!string.IsNullOrWhiteSpace(t)) ShowPopupFor(t!, null);
            else Toast("剪贴板里没有文本");
        }));
        menu.Items.Add(Item("截图", HotkeySpec.Parse(S.HkCaptureOcr).ToString(),
            () => _ = CaptureAsync()));
        menu.Items.Add(new Separator());

        var selection = new MenuItem
        {
            Header = "划词翻译",
            IsCheckable = true,
            IsChecked = S.SelectionMode != SelectionMode.Off,
            InputGestureText = HotkeySpec.Parse(S.HkToggleSelection).ToString(),
        };
        selection.Click += (_, _) => ToggleSelectionMode();
        menu.Items.Add(selection);

        var onTop = new MenuItem { Header = "窗口置顶", IsCheckable = true, IsChecked = S.AlwaysOnTop };
        onTop.Click += (_, _) =>
        {
            S.AlwaysOnTop = !S.AlwaysOnTop;
            if (_main is not null) _main.Topmost = S.AlwaysOnTop;
            SettingsService.Instance.Touch();
        };
        menu.Items.Add(onTop);

        menu.Items.Add(Item("设置…", "", () => ShowSettings()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("打开配置目录", "", OpenConfigDir));
        menu.Items.Add(Item("退出", "", Shutdown));
        return menu;
    }

    /// <summary>托盘菜单锚点窗口的标题，自测靠它在窗口列表里认出那一个。</summary>
    internal const string TrayAnchorTitle = "FlashTrans.TrayAnchor";

    ContextMenu? _trayMenu;
    Window? _trayAnchor;
    DispatcherTimer? _trayWatch;

    /// <summary>
    /// 「点菜单外面自动关掉」靠的是 WPF Popup 的鼠标捕获，而捕获只在我们这条线程
    /// 处于前台时才收得到落在别的程序上的点击。承载托盘的消息窗口是 0×0 且不可见，
    /// SetForegroundWindow 对它无效——菜单就一直挂在屏幕上不走。
    /// 所以在光标处放一个 1×1 的透明窗口当锚点，激活它把线程顶到前台再弹菜单。
    /// </summary>
    internal void ShowTrayMenu()
    {
        CloseTrayMenu();   // 连着右键两次别叠两层

        var anchor = new Window
        {
            Width = 1, Height = 1,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true,
            Title = TrayAnchorTitle,
        };
        var pt = ScreenHelper.ToDip(ScreenHelper.CursorPos());
        anchor.Left = pt.X;
        anchor.Top = pt.Y;
        anchor.SourceInitialized += (_, _) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(anchor).Handle;
            var ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
            // 不进 Alt+Tab，也别接光标底下那一格的点击
            Win32.SetWindowLong(h, Win32.GWL_EXSTYLE,
                ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TRANSPARENT);
        };

        var menu = BuildTrayMenu();
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        // 菜单自己关掉后（选了某项、按了 Esc）把锚点一起收走。
        // 延到 Background 再收：让菜单项的 Click 先跑完，免得刚显示的窗口又被夺回焦点。
        menu.Closed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(
            CloseTrayMenu, DispatcherPriority.Background);

        _trayMenu = menu;
        _trayAnchor = anchor;

        anchor.Show();
        anchor.Activate();
        Win32.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(anchor).Handle);
        menu.IsOpen = true;
        StartTrayWatchdog();
    }

    /// <summary>
    /// 兜底。SetForegroundWindow 有一堆限制（别的程序刚抢过前台、系统正锁着输入等），
    /// 被拒了捕获就还是收不到外面的点击。这里自己盯鼠标：按下时光标不在本进程的窗口上就收摊。
    /// </summary>
    void StartTrayWatchdog()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(70),
        };
        // 唤出菜单的那次右键可能还按着，等所有键松开再开始判，否则一弹出来就自己关了
        var armed = false;
        timer.Tick += (_, _) =>
        {
            if (_trayMenu is not { IsOpen: true }) { CloseTrayMenu(); return; }
            var down = IsAnyMouseButtonDown();
            if (!armed) { armed = !down; return; }
            if (down && !IsCursorOverOwnWindow()) CloseTrayMenu();
        };
        _trayWatch = timer;
        timer.Start();
    }

    static bool IsAnyMouseButtonDown() =>
        (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0
        || (Win32.GetAsyncKeyState(Win32.VK_RBUTTON) & 0x8000) != 0
        || (Win32.GetAsyncKeyState(Win32.VK_MBUTTON) & 0x8000) != 0;

    static bool IsCursorOverOwnWindow()
    {
        var hwnd = Win32.WindowFromPoint(ScreenHelper.CursorPos());
        if (hwnd == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }

    /// <summary>收掉菜单和锚点。先清字段再动手，menu.IsOpen=false 会回调 Closed，靠这个闸挡住重入。</summary>
    internal void CloseTrayMenu()
    {
        var (menu, anchor) = (_trayMenu, _trayAnchor);
        _trayMenu = null;
        _trayAnchor = null;
        _trayWatch?.Stop();
        _trayWatch = null;
        if (menu is null && anchor is null) return;

        try { if (menu is not null) menu.IsOpen = false; }
        catch (Exception ex) { Log.Warn("关托盘菜单失败：" + ex.Message); }
        try { anchor?.Close(); }
        catch (Exception ex) { Log.Warn("关托盘菜单锚点失败：" + ex.Message); }
    }

    static MenuItem Item(string header, string gesture, Action action)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture };
        mi.Click += (_, _) => action();
        return mi;
    }

    static void OpenConfigDir()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsService.Instance.ConfigDir);
            Process.Start(new ProcessStartInfo(SettingsService.Instance.ConfigDir) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("打开配置目录失败：" + ex.Message); }
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        CloseTrayMenu();   // 锚点是普通窗口，留着会挡住进程退出
        _main?.PersistGeometry();
        SettingsService.Instance.Save();
        Application.Current.Shutdown();
    }
}
