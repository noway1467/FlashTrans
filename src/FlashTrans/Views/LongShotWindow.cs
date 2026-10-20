using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>
/// 长图截完之后的预览。长图动不动几千像素高，直接塞剪贴板或者存盘用户也不知道
/// 拼成什么样了，先让他看一眼再挑动作。
/// </summary>
public sealed class LongShotWindow : Window
{
    internal const int PreviewTileHeight = 1024;
    readonly CapturedImage _image;
    readonly ListBox _preview;
    readonly TextBlock _zoomLabel;
    ScrollViewer? _scroll;
    double _zoom = 1;
    DpiScale _previewDpi = new(1, 1);
    bool _fitWidth;
    int _previewRevision;
    double? _pendingSourceOffset;

    /// <summary>用户挑了哪个动作。宿主接着走和普通截图一样的处理。</summary>
    public event Action<CaptureAction>? Action;

    public LongShotWindow(CapturedImage image, string note)
    {
        _image = image;
        Title = $"长截图 {image.Width}×{image.Height}";
        Width = 520;
        Height = 660;
        MinWidth = 360;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SetResourceReference(BackgroundProperty, "Bg");

        var picture = new FrameworkElementFactory(typeof(Image));
        picture.SetBinding(Image.SourceProperty, new Binding(nameof(PreviewTile.Source)));
        picture.SetBinding(WidthProperty, new Binding(nameof(PreviewTile.Width)));
        picture.SetBinding(HeightProperty, new Binding(nameof(PreviewTile.Height)));
        picture.SetBinding(RenderOptions.BitmapScalingModeProperty, new Binding(nameof(PreviewTile.Scaling)));
        picture.SetValue(Image.StretchProperty, Stretch.Fill);
        picture.SetValue(SnapsToDevicePixelsProperty, true);
        var container = new Style(typeof(ListBoxItem));
        container.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        container.Setters.Add(new Setter(MarginProperty, new Thickness(0)));
        container.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        container.Setters.Add(new Setter(FocusableProperty, false));
        container.Setters.Add(new Setter(IsHitTestVisibleProperty, false));
        container.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(ListBoxItem))
        {
            VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)),
        }));

        _preview = new ListBox
        {
            Name = "LongShotPreview", BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(12, 12, 12, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ItemTemplate = new DataTemplate { VisualTree = picture },
            ItemContainerStyle = container,
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
        };
        _preview.Resources[SystemColors.ControlBrushKey] = Brushes.Transparent;
        ScrollViewer.SetHorizontalScrollBarVisibility(_preview, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_preview, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(_preview, true);
        VirtualizingPanel.SetIsVirtualizing(_preview, true);
        VirtualizingPanel.SetVirtualizationMode(_preview, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_preview, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLength(_preview, new VirtualizationCacheLength(0));

        var head = UiKit.Text($"{image.Width} × {image.Height} 像素" + (note.Length > 0 ? "　" + note : ""),
                               12.5, "TextDim", wrap: true);
        head.Margin = new Thickness(14, 10, 14, 0);
        _zoomLabel = UiKit.Text("100%", 12, "TextDim");
        _zoomLabel.VerticalAlignment = VerticalAlignment.Center;
        _zoomLabel.MinWidth = 45;
        var zoomBar = new WrapPanel { Margin = new Thickness(14, 5, 14, 4) };
        zoomBar.Children.Add(ZoomButton("−", "缩小（Ctrl+滚轮）", () => SetPreviewZoom(_zoom / 1.25)));
        zoomBar.Children.Add(_zoomLabel);
        zoomBar.Children.Add(ZoomButton("＋", "放大（Ctrl+滚轮）", () => SetPreviewZoom(_zoom * 1.25)));
        zoomBar.Children.Add(ZoomButton("原始大小", "100%：一个原图像素对应一个屏幕像素（Ctrl+0）", () => SetPreviewZoom(1)));
        zoomBar.Children.Add(ZoomButton("适应宽度", "只缩放预览，复制和保存始终使用无损原图", () => UpdatePreview(_zoom, fitWidth: true)));
        var header = new StackPanel();
        header.Children.Add(head);
        header.Children.Add(zoomBar);

        var bar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(14, 10, 14, 12),
        };
        bar.Children.Add(Btn("识别文字", CaptureAction.Ocr, "GhostBtn"));
        bar.Children.Add(Btn("识别并翻译", CaptureAction.OcrTranslate, "GhostBtn"));
        bar.Children.Add(Btn("钉住", CaptureAction.Pin, "GhostBtn"));
        bar.Children.Add(Btn("复制", CaptureAction.Copy, "OutlineBtn"));
        bar.Children.Add(Btn("保存", CaptureAction.Save, "PrimaryBtn"));

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        UiKit.SetGrid(header);
        UiKit.SetGrid(_preview, row: 1);
        UiKit.SetGrid(bar, row: 2);
        grid.Children.Add(header);
        grid.Children.Add(_preview);
        grid.Children.Add(bar);
        Content = grid;
        UpdatePreview(1, fitWidth: false);
        Loaded += (_, _) =>
        {
            _scroll = FindScrollViewer(_preview);
            UpdatePreview(_zoom, _fitWidth);
        };
        _preview.SizeChanged += (_, _) => { if (_fitWidth) UpdatePreview(_zoom, fitWidth: true); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => UpdatePreview(_zoom, _fitWidth)));
        PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            SetPreviewZoom(_zoom * Math.Pow(1.25, Math.Sign(e.Delta)));
            e.Handled = true;
        };

        PreviewKeyDown += (_, e) =>
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.Escape: e.Handled = true; Fire(CaptureAction.None); break;
                case Key.C when ctrl: e.Handled = true; Fire(CaptureAction.Copy); break;
                case Key.S when ctrl: e.Handled = true; Fire(CaptureAction.Save); break;
                case Key.P when ctrl: e.Handled = true; Fire(CaptureAction.Pin); break;
                case Key.D0 or Key.NumPad0 when ctrl: e.Handled = true; SetPreviewZoom(1); break;
                case Key.D when ctrl:
                    e.Handled = true;
                    Fire(shift ? CaptureAction.OcrTranslate
                               : CaptureOverlay.OcrShortcutAction(
                                   SettingsService.Instance.Current.OcrCopyAndClose));
                    break;
            }
        };
    }

    Button ZoomButton(string text, string hint, Action click)
    {
        var button = new Button
        {
            Content = text, ToolTip = hint, Height = 26,
            Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 6, 0),
        };
        button.SetResourceReference(StyleProperty, "GhostBtn");
        button.Click += (_, _) => click();
        return button;
    }

    internal void SetPreviewZoom(double zoom) => UpdatePreview(zoom, fitWidth: false);

    void UpdatePreview(double zoom, bool fitWidth)
    {
        if (!double.IsFinite(zoom)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (fitWidth)
        {
            var available = Math.Max(1, _preview.ActualWidth - _preview.Padding.Left - _preview.Padding.Right
                - SystemParameters.VerticalScrollBarWidth - 2);
            zoom = Math.Min(1, Math.Floor(available * dpi.DpiScaleX) / _image.Width);
        }
        zoom = Math.Clamp(zoom, fitWidth ? 1.0 / _image.Width : 0.05, 8);
        _fitWidth = fitWidth;
        _zoomLabel.Text = $"{zoom * 100:0}%";
        if (_preview.ItemsSource is not null && Math.Abs(zoom - _zoom) < 0.000001 && dpi.Equals(_previewDpi)) return;

        var sourceOffset = _pendingSourceOffset ?? (_scroll?.VerticalOffset ?? 0) * _previewDpi.DpiScaleY / _zoom;
        _zoom = zoom;
        _previewDpi = dpi;
        // 长图不作为单张超高纹理交给 WPF，避免显卡纹理上限造成整图降采样。
        // 只为可见分片创建位图，复制/保存仍由宿主持有的原始像素完成。
        _preview.ItemsSource = Enumerable.Range(0, (_image.Height + PreviewTileHeight - 1) / PreviewTileHeight)
            .Select(index => new PreviewTile(_image, index, zoom, dpi)).ToArray();
        if (_scroll is not null)
        {
            _pendingSourceOffset = sourceOffset;
            var revision = ++_previewRevision;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (revision != _previewRevision) return;
                _scroll.ScrollToVerticalOffset(sourceOffset * _zoom / _previewDpi.DpiScaleY);
                _pendingSourceOffset = null;
            }));
        }
    }

    internal static Size PreviewSize(int width, int top, int height, double zoom, DpiScale dpi) => new(
        Math.Max(1, Math.Round(width * zoom)) / dpi.DpiScaleX,
        Math.Max(1, Math.Round((top + height) * zoom) - Math.Round(top * zoom)) / dpi.DpiScaleY);

    internal static (int Top, int Height) PreviewTileRows(int imageHeight, int index)
    {
        var count = Math.Max(1, (imageHeight + PreviewTileHeight - 1) / PreviewTileHeight);
        // 均分余数，避免末片特别短导致 WPF 用错误平均高度估算虚拟滚动范围。
        var top = (int)((long)index * imageHeight / count);
        return (top, (int)((long)(index + 1) * imageHeight / count) - top);
    }

    sealed class PreviewTile(CapturedImage image, int index, double zoom, DpiScale dpi)
    {
        readonly (int Top, int Height) _rows = PreviewTileRows(image.Height, index);
        Size Size => PreviewSize(image.Width, _rows.Top, _rows.Height, zoom, dpi);
        public double Width => Size.Width;
        public double Height => Size.Height;
        public BitmapScalingMode Scaling => zoom >= 1 && Math.Abs(zoom - Math.Round(zoom)) < 0.000001
            ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;
        // 不在数据项里缓存 Source；分片滚出虚拟化区域后便可释放像素，避免最终攒满整张长图。
        public BitmapSource Source => LongShotService.Crop(image, _rows.Top, _rows.Height)!.ToBitmap();
    }

    static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }

    Button Btn(string label, CaptureAction action, string style)
    {
        var b = new Button
        {
            Content = label,
            MinWidth = 84,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
        };
        b.SetResourceReference(StyleProperty, style);
        b.Click += (_, _) => Fire(action);
        return b;
    }

    void Fire(CaptureAction action)
    {
        // 只认第一次。Close 到窗口真的消失之间还会处理消息，这中间再按一下
        // Ctrl+S 就会走第二遍：动作干两回（弹两个「另存为」），
        // 而且第二次的 Close 撞在正在关闭的窗口上会抛异常。
        if (_fired) return;
        _fired = true;

        // 先收起自己再干活：存图会弹「另存为」，识别会开主窗口，
        // 这个预览留在后面只是挡视线。
        Close();
        Action?.Invoke(action);
    }

    bool _fired;
}
