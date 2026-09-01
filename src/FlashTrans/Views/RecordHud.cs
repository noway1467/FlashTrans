using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FlashTrans.Interop;

namespace FlashTrans.Views;

/// <summary>
/// 录制中的小浮条：一个闪的红点、录了多久、多少帧，以及怎么停。
///
/// 跟 LongShotHud 一样摆在选区外面——摆里面会被录进去，
/// 而且这条东西上的秒数每帧都在变，录出来满屏都是它在跳。
/// </summary>
public sealed class RecordHud : Window
{
    readonly TextBlock _text;
    readonly Ellipse _dot;
    readonly RECT _region;
    readonly int _maxSeconds;
    int _ticks;
    /// <summary>Esc 已经松开过一次了，从现在起按下才算「停」。见轮询那段。</summary>
    bool _escArmed;

    /// <summary>用户按过 Esc 或者点了浮条。录制那边每帧问一次。</summary>
    public bool Stopped { get; private set; }

    public RecordHud(RECT region, int maxSeconds)
    {
        _region = region;
        _maxSeconds = maxSeconds;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        // 不能抢焦点：录的是别人的窗口，一抢它就失去激活态，标题栏变灰、
        // 光标不闪了，录出来的画面跟用户平时看到的不一样。
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        _text = new TextBlock
        {
            Text = $"录制中… 0.0s / {maxSeconds}s",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEC)),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
        };

        var stop = new TextBlock
        {
            Text = "Esc 停止并保存",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot);
        row.Children.Add(_text);
        row.Children.Add(stop);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x20, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 9),
            Cursor = Cursors.Hand,
            Child = row,
            ToolTip = "点一下停止并保存",
        };
        ((Border)Content).MouseLeftButtonUp += (_, _) => Stopped = true;

        // 自己不接键盘（没焦点），只能盯着 Esc 的实时状态。顺便让红点闪起来。
        var poll = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        poll.Tick += (_, _) =>
        {
            WatchEsc((Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) != 0);

            // 每 8 拍（约 500ms）翻一次，让人一眼看出还在录
            if (++_ticks % 8 == 0) _dot.Opacity = _dot.Opacity > 0.5 ? 0.25 : 1.0;
        };
        poll.Start();
        Closed += (_, _) => poll.Stop();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            // NOACTIVATE：点它也不抢焦点。TOOLWINDOW：不进任务栏，也不被当成可截的窗口。
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
                ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
        };

        Loaded += (_, _) => Place();
    }

    /// <summary>
    /// Esc 的判定。要等它松开过一次才开始算：用户拿 Esc 关掉别的东西之后马上点
    /// 「录制」，或者手还搭在 Esc 上，这时候键是按下状态——直接采信的话，录制刚
    /// 开始就被判成「用户要停」，一帧都没录到，而屏幕上只剩一句「录制没成功」，
    /// 根本看不出是这个原因。
    ///
    /// 单独拎出来是为了能测：真去注入一次 Esc 的话，按键会落到当前有焦点的那个
    /// 窗口上（这个浮条自己是 NOACTIVATE，从不抢焦点），自测不能那么干。
    /// </summary>
    internal void WatchEsc(bool escDown)
    {
        if (!escDown) _escArmed = true;
        else if (_escArmed) Stopped = true;
    }

    public void Report(int frames, TimeSpan elapsed)
        => _text.Text = $"录制中… {elapsed.TotalSeconds:0.0}s / {_maxSeconds}s（{frames} 帧）";

    /// <summary>编码可能要几秒，把浮条留着改个说法，别让屏幕上什么反馈都没有。</summary>
    public void ReportEncoding(int frames)
    {
        _dot.Opacity = 1.0;
        _dot.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
        _text.Text = $"正在编码 {frames} 帧…";
    }

    /// <summary>
    /// 摆在选区正下方居中；下面不够就摆上面；上下都挤不出地方（选区占满屏）
    /// 就贴到工作区底部——那时候难免会被录进去一点，但总比看不见进度好。
    /// </summary>
    void Place()
    {
        UpdateLayout();
        var w = ActualWidth > 0 ? ActualWidth : 260;
        var h = ActualHeight > 0 ? ActualHeight : 36;

        // 选区是物理像素，Left/Top 是 DIP，得换一下
        var tl = ScreenHelper.ToDip(new POINT { X = _region.Left, Y = _region.Top }, this);
        var br = ScreenHelper.ToDip(new POINT { X = _region.Right, Y = _region.Bottom }, this);
        var work = ScreenHelper.WorkAreaAt(new POINT { X = _region.Left, Y = _region.Bottom }, this);

        Left = Math.Clamp((tl.X + br.X) / 2 - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        var top = br.Y + 8;
        if (top + h > work.Bottom) top = tl.Y - h - 8;
        if (top < work.Top) top = work.Bottom - h - 8;
        Top = top;
    }
}
