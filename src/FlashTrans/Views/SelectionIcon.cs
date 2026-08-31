using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlashTrans.Interop;

namespace FlashTrans.Views;

/// <summary>划词后出现的小图标，点一下才翻译，避免误触。</summary>
public sealed class SelectionIcon : Window
{
    readonly DispatcherTimer _autoHide;
    readonly DispatcherTimer _hardLimit;
    string _text = "";

    public Action<string>? Clicked;

    public SelectionIcon()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 30;
        Height = 26;
        Focusable = false;
        ShowActivated = false;

        var badge = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black
            },
            Child = UiKit.Icon(UiKit.IconGlobe, 15),
        };
        badge.SetResourceReference(Border.BackgroundProperty, "BgCard");
        badge.SetResourceReference(Border.BorderBrushProperty, "Accent");
        badge.MouseLeftButtonUp += (_, _) =>
        {
            HideIcon();
            Clicked?.Invoke(_text);
        };
        // 悬停时暂停自动隐藏，但有硬上限兜底：划词后光标常常正好压在图标上，
        // 只靠 MouseLeave 会让它一直留在屏幕上赶不走。
        badge.MouseEnter += (_, _) => _autoHide!.Stop();
        badge.MouseLeave += (_, _) => { _autoHide!.Stop(); _autoHide.Start(); };
        // 右键就地赶走，不用等超时
        badge.MouseRightButtonUp += (_, e) => { e.Handled = true; HideIcon(); };

        Content = badge;

        _autoHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _autoHide.Tick += (_, _) => HideIcon();

        _hardLimit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _hardLimit.Tick += (_, _) => HideIcon();

        Deactivated += (_, _) => HideIcon();
        SourceInitialized += (_, _) => MakeToolWindow();
    }

    void MakeToolWindow()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
    }

    public void ShowAt(Point anchor, string text)
    {
        _text = text;
        var work = ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos(), this);
        var (left, top) = ScreenHelper.PlaceNear(anchor, Width, Height, work, gap: 8);
        Left = left;
        Top = top;

        if (!IsVisible) Show();
        _autoHide.Stop();
        _autoHide.Start();
        _hardLimit.Stop();
        _hardLimit.Start();
    }

    public void HideIcon()
    {
        _autoHide.Stop();
        _hardLimit.Stop();
        if (IsVisible) Hide();
    }
}
