using System.Collections.ObjectModel;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>选区外的实时缩略预览；原图只在后台队列中短暂经过，不作为超高纹理保存。</summary>
public sealed class LongShotLivePreviewWindow : Window
{
    internal const int PreviewPixelWidth = 220;
    internal const int StripeHeight = 512;
    readonly RECT _region;
    readonly TextBlock _status;
    readonly ListBox _preview;
    readonly ObservableCollection<PreviewStripe> _stripes = [];
    readonly Channel<LongShotPreviewPatch> _queue = Channel.CreateUnbounded<LongShotPreviewPatch>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    readonly CancellationTokenSource _lifetime = new();
    readonly Task _worker;
    HwndSource? _source;
    ScrollViewer? _scroll;
    bool _scrollToEnd;
    bool _closed;

    internal Task Completion => _worker;

    public LongShotLivePreviewWindow(RECT region)
    {
        _region = region;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 240;
        Height = 320;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _status = UiKit.Text("实时预览 · 准备截图…", 12, "TextDim", wrap: true);
        _status.Name = "LongShotLiveStatus";
        _status.Margin = new Thickness(8);

        var picture = new FrameworkElementFactory(typeof(Image));
        picture.SetBinding(Image.SourceProperty, new Binding(nameof(PreviewStripe.Source)));
        picture.SetBinding(WidthProperty, new Binding(nameof(PreviewStripe.Width)));
        picture.SetBinding(HeightProperty, new Binding(nameof(PreviewStripe.Height)));
        picture.SetValue(Image.StretchProperty, Stretch.Fill);
        picture.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
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
            Name = "LongShotLiveTiles", BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Padding = new Thickness(8, 0, 8, 8),
            Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Left,
            ItemTemplate = new DataTemplate { VisualTree = picture }, ItemContainerStyle = container,
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
            ItemsSource = _stripes,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_preview, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_preview, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(_preview, true);
        VirtualizingPanel.SetIsVirtualizing(_preview, true);
        VirtualizingPanel.SetVirtualizationMode(_preview, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_preview, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLength(_preview, new VirtualizationCacheLength(0));

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_status);
        UiKit.SetGrid(_preview, row: 1);
        body.Children.Add(_preview);
        var border = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = body,
        };
        border.SetResourceReference(Border.BackgroundProperty, "Bg");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
        Content = border;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
                ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(PreventMouseActivation);
        };
        Loaded += (_, _) => _scroll = FindScrollViewer(_preview);
        IsVisibleChanged += (_, _) => { if (IsVisible) _scrollToEnd = true; };
        _preview.LayoutUpdated += (_, _) =>
        {
            if (!_scrollToEnd || !IsVisible || _scroll is null) return;
            _scrollToEnd = false;
            _scroll.ScrollToEnd();
        };
        SizeChanged += (_, _) => ResizeStripes();

        _worker = Task.Run(ProcessQueueAsync);
        Closed += (_, _) =>
        {
            _closed = true;
            _source?.RemoveHook(PreventMouseActivation);
            _queue.Writer.TryComplete();
            _lifetime.Cancel();
            // 不在 UI 线程同步等后台的 Dispatcher 操作；队列退出后再释放取消源。
            _ = _worker.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _stripes.Clear();
        };
    }

    static IntPtr PreventMouseActivation(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0021) return IntPtr.Zero;
        handled = true;
        return new IntPtr(3); // MA_NOACTIVATE：点击或滚动预览也保留目标窗口焦点。
    }

    /// <summary>左右都放不下就返回 null；预览从不占用被抓取的选区。</summary>
    internal static Rect? TryPlaceOutside(Rect selectionDip, Rect monitorDip,
                                         double preferredWidth = 240, double minWidth = 176)
    {
        if (selectionDip.IsEmpty || monitorDip.IsEmpty || selectionDip.Width <= 0 || selectionDip.Height <= 0
            || !double.IsFinite(preferredWidth) || !double.IsFinite(minWidth) || minWidth <= 0) return null;
        const double gap = 10, edge = 12;
        var maxHeight = monitorDip.Height - edge * 2;
        if (maxHeight <= 0) return null;
        var height = Math.Clamp(selectionDip.Height, Math.Min(240, maxHeight), maxHeight);
        var top = Math.Clamp(selectionDip.Top, monitorDip.Top + edge, monitorDip.Bottom - edge - height);
        preferredWidth = Math.Max(preferredWidth, minWidth);

        var right = Math.Max(selectionDip.Right + gap, monitorDip.Left);
        var rightSpace = monitorDip.Right - right;
        if (rightSpace >= minWidth)
            return new Rect(right, top, Math.Min(preferredWidth, rightSpace), height);

        var leftEnd = Math.Min(selectionDip.Left - gap, monitorDip.Right);
        var leftSpace = leftEnd - monitorDip.Left;
        if (leftSpace >= minWidth)
        {
            var width = Math.Min(preferredWidth, leftSpace);
            return new Rect(leftEnd - width, top, width, height);
        }
        return null;
    }

    /// <summary>隐藏时仍接收增量，用户中途打开预览也能看到完整的拼接进度。</summary>
    public bool TryShowOutside()
    {
        if (_closed) return false;
        var placement = Placement();
        if (placement is null) { Hide(); return false; }
        SetPlacement(placement.Value);
        // 先在隐藏 HWND 上取得实际显示器 DPI，再定位，避免首轮合成落进选区。
        new WindowInteropHelper(this).EnsureHandle();
        placement = Placement();
        if (placement is null) { Hide(); return false; }
        SetPlacement(placement.Value);
        Show();
        return true;
    }

    Rect? Placement()
    {
        var tl = ScreenHelper.ToDip(new POINT { X = _region.Left, Y = _region.Top }, this);
        var br = ScreenHelper.ToDip(new POINT { X = _region.Right, Y = _region.Bottom }, this);
        var monitor = ScreenHelper.MonitorAt(new POINT
        {
            X = _region.Left + (_region.Right - _region.Left) / 2,
            Y = _region.Top + (_region.Bottom - _region.Top) / 2,
        }, this);
        return TryPlaceOutside(new Rect(tl, br), monitor);
    }

    void SetPlacement(Rect bounds)
    {
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    /// <summary>仅入队，不等缩放或 WPF 布局，抓帧循环不被预览拖住。</summary>
    public void Enqueue(LongShotPreviewPatch patch) => _queue.Writer.TryWrite(patch);

    async Task ProcessQueueAsync()
    {
        var token = _lifetime.Token;
        try
        {
            var buffer = new ThumbnailBuffer();
            await foreach (var patch in _queue.Reader.ReadAllAsync(token))
            {
                token.ThrowIfCancellationRequested();
                var update = buffer.Apply(patch);
                await Dispatcher.InvokeAsync(() => ApplyUpdate(update), DispatcherPriority.Background, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error("长截图实时预览失败", ex);
            if (!_closed && !Dispatcher.HasShutdownStarted)
                await Dispatcher.InvokeAsync(() => _status.Text = "预览暂不可用，可继续截图。");
        }
        finally
        {
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out _)) { }
        }
    }

    double DisplayWidth => Math.Max(1, Math.Min(PreviewPixelWidth, Width - 20 - SystemParameters.VerticalScrollBarWidth));

    void ApplyUpdate(ThumbnailUpdate update)
    {
        if (_closed) return;
        while (_stripes.Count > update.FirstChangedStripe) _stripes.RemoveAt(_stripes.Count - 1);
        foreach (var bitmap in update.Sources) _stripes.Add(new PreviewStripe(bitmap, DisplayWidth));
        _status.Text = $"实时预览 · {update.SourceWidth}×{update.SourceHeight}（{update.Frames} 段）";
        _scrollToEnd = true;
    }

    void ResizeStripes()
    {
        for (var i = 0; i < _stripes.Count; i++)
            if (Math.Abs(_stripes[i].Width - DisplayWidth) > 0.01)
                _stripes[i] = new PreviewStripe(_stripes[i].Source, DisplayWidth);
        _scrollToEnd = true;
    }

    internal sealed record PreviewStripe(BitmapSource Source, double Width)
    {
        public double Height => Source.PixelHeight * Width / Source.PixelWidth;
    }

    internal sealed record ThumbnailUpdate(int FirstChangedStripe, BitmapSource[] Sources,
                                           int SourceWidth, int SourceHeight, int Frames);

    /// <summary>按全图原始行号采样；裁掉奇数尾行也不会让后续缩略分片产生累计偏移。</summary>
    internal sealed class ThumbnailBuffer
    {
        readonly List<Stripe> _stripes = [];
        int _sourceWidth;
        int _sourceHeight;
        int _width;

        public ThumbnailUpdate Apply(LongShotPreviewPatch patch)
        {
            if (patch.Reset)
            {
                _stripes.Clear();
                _sourceWidth = patch.TotalWidth;
                _sourceHeight = 0;
                _width = Math.Min(PreviewPixelWidth, _sourceWidth);
            }
            if (_sourceWidth <= 0 || patch.TotalWidth != _sourceWidth
                || patch.TrimTailRows < 0 || patch.TrimTailRows > _sourceHeight)
                throw new InvalidOperationException("实时预览增量的尺寸或尾部裁除范围无效。");

            _sourceHeight -= patch.TrimTailRows;
            var keptRows = PreviewRows(_sourceHeight);
            var keptStripes = (keptRows + StripeHeight - 1) / StripeHeight;
            if (_stripes.Count > keptStripes) _stripes.RemoveRange(keptStripes, _stripes.Count - keptStripes);
            if (keptStripes > 0) _stripes[^1].Rows = keptRows - (keptStripes - 1) * StripeHeight;
            var firstChanged = keptRows / StripeHeight;

            if (patch.AddedSlice is { } slice)
            {
                if (slice.Width != _sourceWidth)
                    throw new InvalidOperationException("实时预览分片宽度与当前视口不一致。");
                var targetRows = PreviewRows(_sourceHeight + slice.Height);
                for (var row = keptRows; row < targetRows; row++)
                {
                    if (_stripes.Count == 0 || _stripes[^1].Rows == StripeHeight)
                        _stripes.Add(new Stripe(new byte[_width * 4 * StripeHeight]));
                    var stripe = _stripes[^1];
                    var sourceY = (int)((long)row * _sourceWidth / _width) - _sourceHeight;
                    var at = stripe.Rows * _width * 4;
                    for (var x = 0; x < _width; x++)
                    {
                        var sourceX = (int)((long)x * _sourceWidth / _width);
                        var pixel = sourceY * slice.Stride + sourceX * 4;
                        slice.Pixels.AsSpan(pixel, 4).CopyTo(stripe.Pixels.AsSpan(at + x * 4, 4));
                    }
                    stripe.Rows++;
                }
                _sourceHeight += slice.Height;
            }
            if (_sourceHeight != patch.TotalHeight)
                throw new InvalidOperationException("实时预览增量的总高度不一致。");

            var sources = _stripes.Skip(firstChanged).Select(stripe =>
            {
                var bitmap = BitmapSource.Create(_width, stripe.Rows, 96, 96,
                    PixelFormats.Bgra32, null, stripe.Pixels, _width * 4);
                bitmap.Freeze();
                return bitmap;
            }).ToArray();
            return new ThumbnailUpdate(firstChanged, sources, _sourceWidth, _sourceHeight, patch.Frames);
        }

        int PreviewRows(int sourceRows) => (int)(((long)sourceRows * _width + _sourceWidth - 1) / _sourceWidth);

        sealed class Stripe(byte[] pixels)
        {
            public byte[] Pixels { get; } = pixels;
            public int Rows { get; set; }
        }
    }

    static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
}
