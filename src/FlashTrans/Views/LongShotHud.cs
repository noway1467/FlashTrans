using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlashTrans.Interop;

namespace FlashTrans.Views;

/// <summary>
/// 长截图进行中的小浮条：说清楚正在滚、滚到多长了、按 Esc 能停。
///
/// 摆在选区外面。摆在里面会被一起拍进长图里——那块区域此刻正在被反复抓帧。
/// </summary>
public sealed class LongShotHud : Window
{
    readonly TextBlock _size;
    readonly RECT _region;

    /// <summary>用户按过 Esc 或者点了「停」。服务那边每帧问一次。</summary>
    public bool Cancelled { get; private set; }

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
            Text = "正在滚动截图…",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEC)),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
        };

        var stop = new TextBlock
        {
            Text = "Esc 停下",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_size);
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
            ToolTip = "点一下停下",
        };
        ((Border)Content).MouseLeftButtonUp += (_, _) => Cancelled = true;

        // 自己不接键盘（没焦点），只能盯着 Esc 的实时状态
        var poll = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        poll.Tick += (_, _) =>
        {
            if ((Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) != 0) Cancelled = true;
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

    public void Report(int height, int frames)
        => _size.Text = $"正在滚动截图…已接 {height} 像素（{frames} 屏）";

    /// <summary>
    /// 摆在选区正下方居中；下面不够就摆上面；上下都挤不出地方（选区占满屏）
    /// 就贴到工作区底部——那时候难免会被拍进去一点，但总比看不见进度好。
    /// </summary>
    void Place()
    {
        UpdateLayout();
        var w = ActualWidth > 0 ? ActualWidth : 220;
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
