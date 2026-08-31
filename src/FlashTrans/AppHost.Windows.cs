using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
        _popup?.HidePopup();
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

    void ShowTrayMenu()
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

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

        // 托盘菜单需要一个前台窗口才能正常关闭，用 IsOpen 直接弹
        menu.IsOpen = true;
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
        _main?.PersistGeometry();
        SettingsService.Instance.Save();
        Application.Current.Shutdown();
    }
}
