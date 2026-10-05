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
    readonly CapturedImage _image;
    readonly Image _picture;
    readonly Border _toolbar;
    int _pixelWidth;
    int _pixelHeight;
    bool _closing;

    public event Action? CopyRequested;
    public event Action? SaveRequested;

    public PinnedShotWindow(CapturedImage image, RECT? region = null)
    {
        _region = region;
        _image = image;
        _pixelWidth = image.Width;
        _pixelHeight = image.Height;
        Title = "钉住的截图";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Background = Brushes.Transparent;
        AllowsTransparency = true;

        _picture = new Image
        {
            Source = image.ToBitmap(),
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.NearestNeighbor);

        _toolbar = BuildToolbar();
        var grid = new Grid();
        grid.Children.Add(_picture);
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
        // 显示到目标屏幕后再校准一次，避免首次创建 HWND 时沿用另一块屏幕的 DPI。
        Loaded += (_, _) => Place();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded, new Action(KeepPixelSize));
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
            Visibility = Visibility.Collapsed,
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
        int left, top;
        if (_region is { } r)
        {
            left = r.Left;
            top = r.Top;
            // 区域只决定位置；显示尺寸必须来自实际图片，否则一像素的边界误差也会被拉伸。
            _pixelWidth = _image.Width;
            _pixelHeight = _image.Height;
        }
        else
        {
            var area = ScreenHelper.WorkAreaAt(ScreenHelper.CursorPos(), this);
            var (sx, sy) = DpiScale(this);
            var fit = Math.Min(1, Math.Min(Math.Min(680, area.Width * 0.8) * sx / _image.Width,
                Math.Min(720, area.Height * 0.8) * sy / _image.Height));
            _pixelWidth = Math.Max(1, (int)Math.Round(_image.Width * fit));
            _pixelHeight = Math.Max(1, (int)Math.Round(_image.Height * fit));
            left = (int)Math.Round(area.Left * sx + (area.Width * sx - _pixelWidth) / 2);
            top = (int)Math.Round(area.Top * sy + (area.Height * sy - _pixelHeight) / 2);
        }

        RenderOptions.SetBitmapScalingMode(_picture,
            _pixelWidth == _image.Width && _pixelHeight == _image.Height
                ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, left, top, _pixelWidth, _pixelHeight, Win32.SWP_NOACTIVATE);
    }

    void KeepPixelSize()
    {
        if (_closing) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        // 跨 DPI 屏幕拖动时仍保持物理像素大小，不让 WPF 把截图当普通界面整体放大。
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, _pixelWidth, _pixelHeight,
            Win32.SWP_NOMOVE | Win32.SWP_NOACTIVATE);
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
