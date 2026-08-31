using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using FlashTrans.Interop;

namespace FlashTrans.Views;

/// <summary>
/// 右下角的轻提示，替掉托盘气泡。
/// 系统气泡受「专注助手」和通知开关管，经常一声不响就被吞掉；
/// 这个是自己画的窗口，样式跟着主题走，也一定能看见。
/// </summary>
public sealed class ToastWindow : Window
{
    readonly TextBlock _text = UiKit.Text("", 12.5, "Text", wrap: true);
    readonly DispatcherTimer _hide;
    static ToastWindow? _current;

    ToastWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;      // 提示不该把焦点从用户正在打字的地方抢走
        Focusable = false;
        // 宽度也跟着内容走：固定宽度下「已开启」这种短句右边会空一大块。
        SizeToContent = SizeToContent.WidthAndHeight;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;

        _text.MaxWidth = 240;       // 长句到这里换行，不会横着长满屏
        _text.LineHeight = 18;

        var icon = UiKit.Icon(UiKit.IconGlobe, 15);
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 0, 0);

        var row = UiKit.Row(9, icon, _text);
        row.VerticalAlignment = VerticalAlignment.Center;

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 11),
            Margin = new Thickness(12),
            MinWidth = 150,
            Cursor = Cursors.Hand,
            Child = row,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.4, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "BgCard");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.MouseLeftButtonUp += (_, _) =>
        {
            var act = _onClick;
            Dismiss();
            act?.Invoke();
        };
        Content = card;

        _hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hide.Tick += (_, _) => Dismiss();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
                ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
        };
    }

    /// <summary>
    /// 弹一条提示。连续调用会复用同一个窗口，后一条顶掉前一条。
    /// onClick 传了的话，点这条提示会先做那件事再收起来（比如打开刚存好的图）。
    /// </summary>
    public static void Show(string message, Action? onClick = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        try
        {
            _current ??= new ToastWindow();
            _current.Display(message, onClick);
        }
        catch (Exception ex)
        {
            Services.Log.Warn("提示窗口失败：" + ex.Message);
            _current = null;
        }
    }

    Action? _onClick;

    void Display(string message, Action? onClick)
    {
        _text.Text = message;
        _onClick = onClick;
        _hide.Stop();
        BeginAnimation(OpacityProperty, null);   // 松开淡出动画对 Opacity 的占用
        Opacity = 1;

        if (!IsVisible)
        {
            // 先丢到屏幕外再显示：高度是自适应的，摆之前量不到，
            // 直接 Show 会有一帧闪在左上角。
            Left = -20000;
            Top = -20000;
            Show();
        }
        UpdateLayout();

        // 宽高都是自适应的，摆之前必须读实测值，不能用 Width（那是 NaN）
        var w = ActualWidth > 0 ? ActualWidth : 280;
        var h = ActualHeight > 0 ? ActualHeight : 64;
        var work = ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos(), this);
        Left = work.Right - w;
        Top = work.Bottom - h - 6;

        _hide.Start();
    }

    void Dismiss()
    {
        _hide.Stop();
        if (!IsVisible) return;

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            // 淡出过程中又来了新提示的话，Opacity 已经被 Display 重置成 1，别再藏了
            if (Opacity < 0.05) Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>退出时收掉，别让隐藏的窗口拖着进程。</summary>
    public static void Shutdown()
    {
        try { _current?.Close(); } catch { /* 忽略 */ }
        _current = null;
    }
}
