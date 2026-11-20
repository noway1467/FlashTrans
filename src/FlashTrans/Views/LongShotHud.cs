using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>
/// 长截图浮条：切换手动/自动滚动、显示进度，完成后预览已接好的长图。
///
/// 摆在选区外面。摆在里面会被一起拍进长图里——那块区域此刻正在被反复抓帧。
/// 别指望 WDA_EXCLUDEFROMCAPTURE 兜底，见 <see cref="Win32.WDA_EXCLUDEFROMCAPTURE"/>：
/// 对本项目这种 BitBlt 抓屏，隐身的窗口在图里是一块纯黑，比拍到浮条还糟。
/// </summary>
public sealed class LongShotHud : Window
{
    readonly TextBlock _size;
    readonly Button _mode;
    readonly RECT _region;
    int _height;
    int _frames;
    bool _alignmentLost;
    bool _escArmed;
    string? _automaticPause;

    /// <summary>躲不开选区。见 <see cref="Report"/>：这时候不能再改字。</summary>
    bool _quiet;

    /// <summary>用户按过 Esc 或者点了「完成」。服务那边每帧问一次。</summary>
    public bool Cancelled { get; private set; }

    /// <summary>先让用户决定滚哪个容器，只有主动开启后才模拟滚轮。</summary>
    public bool Automatic { get; private set; }

    public LongShotHud(RECT region)
    {
        _region = region;

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
            ToolTip = "用滚轮或拖动页面滚动条，每次保留部分重叠内容；暂时不滚不会结束。",
        };
        _size.SetResourceReference(TextBlock.ForegroundProperty, "Text");

        _mode = new Button
        {
            Name = "LongShotMode", Content = "自动滚动", Height = 32, Focusable = false,
            ToolTip = "自动向下滚动；页面不响应时可切回手动滚动。", IsEnabled = false,
        };
        _mode.SetResourceReference(StyleProperty, "GhostBtn");
        _mode.Click += (_, _) =>
        {
            Automatic = !Automatic;
            _mode.Content = Automatic ? "手动滚动" : "自动滚动";
            _mode.ToolTip = Automatic ? "停止自动滚动，改用滚轮或拖动页面滚动条。"
                : "自动向下滚动；页面不响应时可切回手动滚动。";
            _alignmentLost = false;
            _automaticPause = null;
            RefreshStatus();
        };

        var stop = new Button
        {
            Name = "LongShotFinish", Content = "完成（Esc）", Height = 32, Focusable = false,
            ToolTip = "结束滚动截图，预览并保存已接好的内容。",
        };
        stop.SetResourceReference(StyleProperty, "GhostBtn");
        stop.Click += (_, _) => Cancelled = true;

        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_size);
        row.Children.Add(_mode);
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
        var poll = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        poll.Tick += (_, _) =>
        {
            var down = (Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) != 0;
            if (!down) _escArmed = true;
            else if (_escArmed) Cancelled = true;
        };
        poll.Start();
        Closed += (_, _) => { Cancelled = true; poll.Stop(); };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            // NOACTIVATE：点它也不抢焦点。TOOLWINDOW：不进任务栏，也不被当成可截的窗口。
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
                ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
            HwndSource.FromHwnd(hwnd)?.AddHook(PreventMouseActivation);
        };

        Loaded += (_, _) => Place();
    }

    static IntPtr PreventMouseActivation(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WPF 的按钮会处理 WM_MOUSEACTIVATE，单设 WS_EX_NOACTIVATE 仍会抢走页面焦点。
        if (message != 0x0021) return IntPtr.Zero;
        handled = true;
        return new IntPtr(3); // MA_NOACTIVATE：保留鼠标点击，但不激活浮条。
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
        _mode.IsEnabled = true;
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
        _mode.Content = "自动滚动";
        _mode.ToolTip = "重新尝试自动滚动，也可继续手动滚动或点击完成。";
        _alignmentLost = false;
        _automaticPause = reason == LongShotStop.Bottom
            ? "画面未继续滚动，已切到手动"
            : "画面暂未对齐，已切到手动";
        RefreshStatus();
    }

    void RefreshStatus()
    {
        if (_quiet) return;
        var state = _alignmentLost ? "未找到共同内容：请保留更多重叠区域"
            : _automaticPause ?? (Automatic ? "自动滚动" : "手动：向下慢滚");
        _size.Text = $"{state} · {_height} 像素（{_frames} 段）";
        // 字变长了，位置得跟着重算，不然会从选区边上长回选区里
        Place();
    }

    /// <summary>
    /// 摆在选区外面。选区把工作区占满时（照着最大化窗口选的网页，最常见的一种）
    /// 就压到任务栏上去——那条缝是唯一躲得开的地方。
    /// </summary>
    void Place()
    {
        var mon = ScreenHelper.MonitorAt(new POINT { X = _region.Left, Y = _region.Bottom }, this);
        MaxWidth = Math.Max(1, mon.Width - 16);
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
