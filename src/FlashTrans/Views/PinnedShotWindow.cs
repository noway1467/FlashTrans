using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>
/// 钉在屏幕上的截图。默认沿用截图时那块屏幕位置，置顶显示；
/// 销毁只是把这块临时查看窗关掉，不会动已经另存的文件。
/// </summary>
public sealed class PinnedShotWindow : Window
{
    static readonly Geometry IconSave = Geometry.Parse(
        "M4,2 H11 L12.5,3.5 V14 H4 Z M6,2 V6 H10 V2 M6,9 H10 V14 H6 Z");
    readonly RECT? _region;
    readonly Border _toolbar;
    bool _closing;

    public event Action? CopyRequested;
    public event Action? SaveRequested;

    public PinnedShotWindow(CapturedImage image, RECT? region = null)
    {
        _region = region;
        Title = "钉住的截图";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Background = Brushes.Transparent;
        AllowsTransparency = true;

        var pic = new Image
        {
            Source = image.ToBitmap(),
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.NearestNeighbor);

        _toolbar = BuildToolbar();
        var grid = new Grid();
        grid.Children.Add(pic);
        grid.Children.Add(_toolbar);
        Content = grid;

        MouseEnter += (_, _) => _toolbar.Visibility = Visibility.Visible;
        MouseLeave += (_, _) => _toolbar.Visibility = Visibility.Collapsed;
        MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); }
            catch { /* 鼠标已松开时 DragMove 会抛，这里不用处理 */ }
        };
        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape or Key.Delete:
                    e.Handled = true;
                    Destroy();
                    break;
                case Key.C when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                    e.Handled = true;
                    CopyRequested?.Invoke();
                    break;
                case Key.S when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                    e.Handled = true;
                    SaveRequested?.Invoke();
                    break;
            }
        };
        SourceInitialized += (_, _) => Place();
        Closing += (_, _) => _closing = true;
    }

    Border BuildToolbar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(UiKit.IconButton(UiKit.IconCopy, "复制截图", (_, _) => CopyRequested?.Invoke()));
        row.Children.Add(UiKit.IconButton(IconSave, "保存截图", (_, _) => SaveRequested?.Invoke()));
        row.Children.Add(UiKit.IconButton(UiKit.IconTrash, "销毁钉住 (Esc / Delete)", (_, _) => Destroy()));

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x20, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 3, 5, 3),
            Margin = new Thickness(7),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = row,
        };
        border.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return border;
    }

    /// <summary>关掉钉住窗。不给窗口留“半关闭”状态，反复点销毁也安全。</summary>
    public void Destroy()
    {
        if (_closing || !IsVisible) return;
        _closing = true;
        Close();
    }

    void Place()
    {
        if (_region is { } r)
        {
            var topLeft = ScreenHelper.ToDip(new POINT { X = r.Left, Y = r.Top }, this);
            var (sx, sy) = DpiScale(this);
            Left = topLeft.X;
            Top = topLeft.Y;
            Width = Math.Max(8, (r.Right - r.Left) / sx);
            Height = Math.Max(8, (r.Bottom - r.Top) / sy);
            return;
        }

        var area = ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos(), this);
        Width = Math.Min(680, area.Width * 0.8);
        Height = Math.Min(720, area.Height * 0.8);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    static (double X, double Y) DpiScale(Window w)
    {
        try
        {
            var m = System.Windows.PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice;
            if (m is { } t && t.M11 > 0 && t.M22 > 0) return (t.M11, t.M22);
        }
        catch { /* 没有 PresentationSource 时退回主屏 DPI */ }

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(new Border());
        return (dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX, dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY);
    }
}
