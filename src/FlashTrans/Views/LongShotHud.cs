using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>
/// 长截图浮条：开始/暂停自动滚动、切回手动、开关预览和完成截图。
///
/// 摆在选区外面。摆在里面会被一起拍进长图里——那块区域此刻正在被反复抓帧。
/// 别指望 WDA_EXCLUDEFROMCAPTURE 兜底，见 <see cref="Win32.WDA_EXCLUDEFROMCAPTURE"/>：
/// 对本项目这种 BitBlt 抓屏，隐身的窗口在图里是一块纯黑，比拍到浮条还糟。
/// </summary>
public sealed class LongShotHud : Window
{
    const int ToggleHotkeyId = 0xA1F2;
    readonly TextBlock _size;
    readonly Button _mode;
    readonly Button _manual;
    readonly Button _preview;
    readonly RECT _region;
    readonly HotkeySpec _toggleSpec;
    readonly string _toggleLabel;
    readonly bool _autoStart;
    readonly Action<bool>? _onLivePreviewToggled;
    readonly System.Windows.Threading.DispatcherTimer _poll;
    HwndSource? _source;
    IntPtr _hwnd;
    int _height;
    int _frames;
    bool _ready;
    bool _alignmentLost;
    bool _escArmed;
    bool _toggleArmed;
    bool _toggleChordDown;
    bool _hotkeyOk;
    string? _automaticPause;
    string? _previewNotice;

    /// <summary>躲不开选区。见 <see cref="Report"/>：这时候不能再改字。</summary>
    bool _quiet;

    /// <summary>用户按过 Esc 或者点了「完成」。服务那边每帧问一次。</summary>
    public bool Cancelled { get; private set; }

    /// <summary>先让用户决定滚哪个容器，只有主动开启后才模拟滚轮。</summary>
    public bool Automatic { get; private set; }

    /// <summary>暂停连手动抓帧也停住；切回手动后才继续观察用户滚动。</summary>
    public bool Paused { get; private set; }

    public bool LivePreviewVisible { get; private set; }

    public LongShotHud(RECT region, string? toggleHotkey = null, bool autoStart = false,
                      bool livePreviewOn = false, Action<bool>? onLivePreviewToggled = null)
    {
        _region = region;
        var spec = HotkeySpec.Parse(toggleHotkey ?? SettingsService.Instance.Current.LongShotToggleHotkey);
        _toggleSpec = spec.IsValidGlobal ? spec : HotkeySpec.None;
        _toggleLabel = _toggleSpec.IsEmpty ? "" : $" ({_toggleSpec})";
        _autoStart = autoStart;
        _onLivePreviewToggled = onLivePreviewToggled;
        LivePreviewVisible = livePreviewOn;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        // 不能抢焦点：滚轮消息要送给被截的那个窗口
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _size = new TextBlock
        {
            Text = "准备截图…",
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Margin = new Thickness(0, 0, 8, 0),
            TextWrapping = TextWrapping.Wrap,
            ToolTip = "用滚轮或拖动页面滚动条，每次保留部分重叠内容；暂时不滚不会结束。",
        };
        _size.SetResourceReference(TextBlock.ForegroundProperty, "Text");

        _mode = HudButton("LongShotMode", "自动滚动" + _toggleLabel,
            "开始或暂停自动向下滚动。", ToggleScroll);
        _mode.IsEnabled = false;
        _manual = HudButton("LongShotManual", "手动滚动",
            "停止自动滚动，改用滚轮或拖动页面滚动条。", UseManualScroll);
        _manual.IsEnabled = false;
        _preview = HudButton("LongShotPreview", livePreviewOn ? "隐藏预览" : "实时预览",
            "在选区外侧显示实时拼接效果。", ToggleLivePreview);
        _preview.Visibility = onLivePreviewToggled is null ? Visibility.Collapsed : Visibility.Visible;
        var stop = HudButton("LongShotFinish", "完成（Esc）",
            "结束滚动截图，处理已接好的内容。", () => Cancelled = true);

        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_size);
        row.Children.Add(_mode);
        row.Children.Add(_manual);
        row.Children.Add(_preview);
        row.Children.Add(stop);

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 4, 8, 4),
            Child = row,
        };
        border.SetResourceReference(Border.BackgroundProperty, "Bg");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        Content = border;

        // 自己不接键盘（没焦点），只能盯着 Esc 的实时状态
        _poll = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _poll.Tick += (_, _) =>
        {
            WatchEsc((Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) != 0);
            if (_toggleSpec.IsEmpty) return;
            var down = ToggleChordDown();
            if (!_hotkeyOk) WatchToggleChord(down);
            else if (!down) _toggleArmed = true;
        };
        _poll.Start();
        Closed += (_, _) =>
        {
            Cancelled = true;
            _poll.Stop();
            if (_hotkeyOk) Win32.UnregisterHotKey(_hwnd, ToggleHotkeyId);
            _source?.RemoveHook(WndProc);
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = Win32.GetWindowLong(_hwnd, Win32.GWL_EXSTYLE);
            // NOACTIVATE：点它也不抢焦点。TOOLWINDOW：不进任务栏，也不被当成可截的窗口。
            Win32.SetWindowLong(_hwnd, Win32.GWL_EXSTYLE,
                ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
            if (!_toggleSpec.IsEmpty)
            {
                _hotkeyOk = Win32.RegisterHotKey(_hwnd, ToggleHotkeyId,
                    _toggleSpec.Win32Modifiers, _toggleSpec.VirtualKey);
                _toggleArmed = !ToggleChordDown();
                if (!_hotkeyOk) Log.Warn($"长截图热键 {_toggleSpec} 注册失败，改用轮询");
            }
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
        };

        Loaded += (_, _) => Place();
    }

    static Button HudButton(string name, string label, string hint, Action click)
    {
        var button = new Button
        {
            Name = name, Content = label, ToolTip = hint, Height = 32, FontSize = 12.5,
            Focusable = false, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 4, 0),
        };
        button.SetResourceReference(StyleProperty, "GhostBtn");
        button.Click += (_, _) => click();
        return button;
    }

    IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Win32.WM_HOTKEY && wParam.ToInt32() == ToggleHotkeyId)
        {
            handled = true;
            if (_toggleArmed) ToggleScroll();
            return IntPtr.Zero;
        }
        // WPF 的按钮会处理 WM_MOUSEACTIVATE，单设 WS_EX_NOACTIVATE 仍会抢走页面焦点。
        if (message != 0x0021) return IntPtr.Zero;
        handled = true;
        return new IntPtr(3); // MA_NOACTIVATE：保留鼠标点击，但不激活浮条。
    }

    bool ToggleChordDown()
    {
        if (_toggleSpec.IsEmpty || (Win32.GetAsyncKeyState((int)_toggleSpec.VirtualKey) & 0x8000) == 0) return false;
        var modifiers = ModifierKeys.None;
        if ((Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0) modifiers |= ModifierKeys.Control;
        if ((Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) != 0) modifiers |= ModifierKeys.Alt;
        if ((Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) != 0) modifiers |= ModifierKeys.Shift;
        if ((Win32.GetAsyncKeyState(Win32.VK_LWIN) & 0x8000) != 0
            || (Win32.GetAsyncKeyState(0x5C) & 0x8000) != 0) modifiers |= ModifierKeys.Windows;
        return modifiers == _toggleSpec.Modifiers;
    }

    internal void WatchToggleChord(bool down)
    {
        if (!down) _toggleArmed = true;
        else if (_toggleArmed && !_toggleChordDown) ToggleScroll();
        _toggleChordDown = down;
    }

    internal void WatchEsc(bool escDown)
    {
        if (!escDown) _escArmed = true;
        else if (_escArmed) Cancelled = true;
    }

    internal void ToggleScroll()
    {
        if (!_ready || Cancelled) return;
        if (Automatic && !Paused) { Automatic = false; Paused = true; }
        else { Automatic = true; Paused = false; }
        _alignmentLost = false;
        _automaticPause = null;
        RefreshStatus();
    }

    internal void UseManualScroll()
    {
        if (!_ready || Cancelled) return;
        Automatic = false;
        Paused = false;
        _alignmentLost = false;
        _automaticPause = null;
        RefreshStatus();
    }

    internal void ToggleLivePreview()
    {
        if (_onLivePreviewToggled is null || Cancelled) return;
        LivePreviewVisible = !LivePreviewVisible;
        _previewNotice = null;
        RefreshStatus();
        _onLivePreviewToggled(LivePreviewVisible);
    }

    /// <summary>宿主确认实际显示结果，空间不足时保留可读提示，不把预览塞进选区。</summary>
    public void ReportLivePreview(bool visible, string? notice = null)
    {
        LivePreviewVisible = visible;
        _previewNotice = notice;
        RefreshStatus();
    }

    /// <summary>
    /// 报进度。浮条压在选区里的时候，这行字一个字都不能改——每帧变一次的数字会
    /// 毁掉整张长图：页面明明已经滚到底、两帧本该一模一样，就因为这几个数字不同
    /// 被判成「画面还在动」，于是拿重复的页脚去对齐，把同一屏底部反复接上十几遍。
    /// 冻住的浮条就只是图里多了一块死东西，接得还是对的。
    /// </summary>
    public void Report(int height, int frames)
    {
        if (frames > _frames) _automaticPause = null;
        _height = height;
        _frames = frames;
        if (!_ready && frames > 0)
        {
            _ready = true;
            Automatic = _autoStart;
            Paused = false;
        }
        RefreshStatus();
    }

    public void ReportAlignmentLost(bool lost)
    {
        if (_alignmentLost == lost) return;
        _alignmentLost = lost;
        RefreshStatus();
    }

    public void PauseAutomatic(LongShotStop reason)
    {
        Automatic = false;
        Paused = false;
        _alignmentLost = false;
        _automaticPause = reason == LongShotStop.Bottom
            ? "画面未继续滚动，已切到手动"
            : "画面暂未对齐，已切到手动";
        RefreshStatus();
    }

    void RefreshStatus()
    {
        _mode.IsEnabled = _ready && !Cancelled;
        _manual.IsEnabled = _ready && !Cancelled && (Automatic || Paused);
        if (_quiet) return;
        _mode.Content = (Automatic ? "暂停滚动" : Paused ? "继续滚动" : "自动滚动") + _toggleLabel;
        _mode.ToolTip = Automatic ? "暂停滚动与抓帧，保留已接好的内容。"
            : "自动向下滚动；也可切回手动微调。";
        _preview.Content = LivePreviewVisible ? "隐藏预览" : "实时预览";
        var state = _previewNotice ?? (Paused ? "已暂停" + _toggleLabel
            : _alignmentLost ? "未找到共同内容：请保留更多重叠区域"
            : _automaticPause ?? (Automatic ? "自动滚动" + _toggleLabel : "手动：向下慢滚" + _toggleLabel));
        _size.Text = _ready ? $"{state} · {_height} 像素（{_frames} 段）" : _previewNotice ?? "准备截图…";
        // 字变长了，位置得跟着重算，不然会从选区边上长回选区里
        if (IsLoaded) Place();
    }

    /// <summary>
    /// 摆在选区外面。选区把工作区占满时（照着最大化窗口选的网页，最常见的一种）
    /// 就压到任务栏上去——那条缝是唯一躲得开的地方。
    /// </summary>
    void Place()
    {
        var mon = ScreenHelper.MonitorAt(new POINT { X = _region.Left, Y = _region.Bottom }, this);
        MaxWidth = Math.Max(1, mon.Width - 16);
        var available = SizeToContent == SizeToContent.Height && double.IsFinite(Width)
            ? Math.Min(Width, MaxWidth) : MaxWidth;
        _size.MaxWidth = Math.Max(1, available - 26);
        foreach (var button in new[] { _mode, _manual, _preview }) button.MaxWidth = Math.Max(1, available - 22);
        UpdateLayout();
        var w = ActualWidth > 0 ? ActualWidth : 220;
        var h = ActualHeight > 0 ? ActualHeight : 36;

        // 选区是物理像素，Left/Top 是 DIP，得换一下
        var tl = ScreenHelper.ToDip(new POINT { X = _region.Left, Y = _region.Top }, this);
        var br = ScreenHelper.ToDip(new POINT { X = _region.Right, Y = _region.Bottom }, this);
        var sel = new Rect(tl, br);
        // 要整块屏幕而不是工作区，任务栏那条缝就是靠这个让出来的
        (Left, Top) = ScreenHelper.PlaceOutside(sel, mon, w, h);

        var box = new Rect(Left, Top, w, h);
        var over = Rect.Intersect(box, sel);
        _quiet = !over.IsEmpty && over.Width * over.Height > 0;
        if (_quiet && _height == 0) _size.Text = "长截图";
    }
}
