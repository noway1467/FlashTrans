using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>
/// 截图的结果：用户按了哪个动作，以及那块图。
/// Region 是选区在屏幕物理像素里的位置，长截图要拿它去接着往下滚。
/// </summary>
public sealed record CaptureOutcome(CaptureAction Action, CapturedImage? Image, RECT Region)
{
    public static readonly CaptureOutcome Cancelled = new(CaptureAction.None, null, default);
    /// <summary>用户点了长截图，还没真正截完——宿主要接着走滚动拼接那条路。</summary>
    public bool WantsLongShot { get; init; }
}

/// <summary>
/// 截图。先把整个虚拟桌面抓成一张静态图铺满屏幕，再让用户在这张图上框选、
/// 画标注，最后从工具条挑一个动作（复制、保存、识别、识别并翻译、长截图）。
///
/// 抓成静态图有三个好处：蒙层自己不会被拍进去；屏幕上的动画不会在拖动过程中变；
/// 选区坐标直接落在图的像素上，多显示器不同 DPI 也不用换算。
/// </summary>
public sealed partial class CaptureOverlay : Window
{
    readonly CapturedImage _shot;
    readonly RECT _screen;
    readonly CaptureSelectionLayer _layer;
    readonly Canvas _canvas = new();
    readonly Border _toolbar;
    /// <summary>弹蒙层之前的窗口列表，空格键靠它套窗口。</summary>
    readonly List<RECT> _windows;
    TextBox? _textBox;
    CaptureOutcome _result = CaptureOutcome.Cancelled;
    bool _everActive;
    bool _closing;

    /// <summary>
    /// 走一遍截图。返回用户选了哪个动作和那块图；取消返回 Action=None。
    /// 这个调用会一直阻塞到用户做完。
    /// </summary>
    public static CaptureOutcome Pick()
    {
        var screen = ScreenCapture.VirtualScreen();
        var shot = ScreenCapture.Grab(screen);
        if (shot is null) return CaptureOutcome.Cancelled;

        var win = new CaptureOverlay(shot, screen);
        win.Show();
        win.PlaceOverVirtualScreen();
        win.Activate();
        win.Focus();

        // 自己起一个消息帧当模态用。不走 ShowDialog：那会禁掉本线程所有窗口，
        // 而这里只是暂时借用屏幕，主窗口没必要被冻住。
        var frame = new DispatcherFrame();
        win.Closed += (_, _) => frame.Continue = false;
        Dispatcher.PushFrame(frame);
        return win._result;
    }

    CaptureOverlay(CapturedImage shot, RECT screen)
    {
        _shot = shot;
        _screen = screen;
        // 趁自己还没显示出来先记下窗口，等 Show 之后就只看得见蒙层了
        _windows = TopLevelWindows();

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;

        _layer = new CaptureSelectionLayer(shot)
        {
            PenColor = ParseColor(S.CapturePenColor),
            PenWidth = CaptureLimits.ClampPenWidth(S.CapturePenWidth),
            TextSize = CaptureLimits.ClampFontSize(S.CaptureFontSize),
            TextBold = S.CaptureFontBold,
            TextItalic = S.CaptureFontItalic,
            MosaicBlock = CaptureLimits.ClampMosaicBlock(S.CaptureMosaicBlock),
        };
        _layer.Cancelled += Close;
        _layer.Committed += () => Finish(S.CaptureEnterAction);
        _layer.SelectionChanged += OnSelectionChanged;
        _layer.TextRequested += StartTextInput;
        // 点中一段文字时第二行要换成字号那一排——没选工具时也得能改刚写的字
        _layer.ActiveChanged += SyncToolbar;

        _toolbar = BuildToolbar();
        _toolbar.Visibility = Visibility.Collapsed;

        _canvas.Children.Add(_layer);
        _canvas.Children.Add(_toolbar);
        Content = _canvas;

        // 层要铺满整个画布。Canvas 不会自己撑孩子，得跟着尺寸走。
        SizeChanged += (_, _) =>
        {
            _layer.Width = _canvas.ActualWidth;
            _layer.Height = _canvas.ActualHeight;
        };

        PreviewKeyDown += OnKey;
        // 焦点被别的程序抢走时直接收摊，免得留一层盖住整个屏幕的窗口下不去。
        // 要等真的激活过一次再看：Show 到 Activate 之间也会过一次 Deactivated，
        // 那时候就关等于刚弹出来就自己消失了。
        Activated += (_, _) => _everActive = true;
        Deactivated += (_, _) => { if (_everActive) CloseOnce(); };
        Closing += (_, _) => _closing = true;
    }

    /// <summary>
    /// 关窗口，但只关一次。
    ///
    /// 自己 Close 的过程中焦点会被交出去，那会再触发一次 Deactivated；
    /// 上面那个处理器又调 Close，WPF 就在 VerifyNotClosing 里抛
    /// 「Cannot ... Close ... while a Window is closing」。这异常从 WndProc 里冒出来，
    /// 栈里一帧本程序的代码都没有，光看日志根本看不出是谁干的。
    /// </summary>
    void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    static AppSettings S => SettingsService.Instance.Current;

    // ------------------------------------------------------------- 结束

    /// <summary>按某个动作收尾。导出失败（没选区）就什么都不做，让用户继续。</summary>
    void Finish(CaptureAction action)
    {
        if (action == CaptureAction.None) { CloseOnce(); return; }
        if (!_layer.HasSelection) return;

        var img = _layer.Export();
        if (img is null) return;

        _result = new CaptureOutcome(action, img, SelectionInScreen());
        CloseOnce();
    }

    /// <summary>选区换算到屏幕物理像素。长截图要在这块区域上接着滚。</summary>
    RECT SelectionInScreen()
    {
        var sel = _layer.Selection;
        var sx = _layer.ActualWidth > 0 ? _shot.Width / _layer.ActualWidth : 1;
        var sy = _layer.ActualHeight > 0 ? _shot.Height / _layer.ActualHeight : 1;
        return new RECT
        {
            Left = _screen.Left + (int)Math.Round(sel.Left * sx),
            Top = _screen.Top + (int)Math.Round(sel.Top * sy),
            Right = _screen.Left + (int)Math.Round(sel.Right * sx),
            Bottom = _screen.Top + (int)Math.Round(sel.Bottom * sy),
        };
    }

    /// <summary>长截图：不在这儿截，把选区交出去让宿主慢慢滚。</summary>
    void FinishLongShot()
    {
        if (!_layer.HasSelection) return;
        _result = new CaptureOutcome(CaptureAction.None, null, SelectionInScreen()) { WantsLongShot = true };
        CloseOnce();
    }

    // ------------------------------------------------------------- 坐标

    /// <summary>
    /// 按物理像素摆到整个虚拟桌面上。不用 Left/Top/Width/Height：那些是 DIP，
    /// 多显示器不同缩放时窗口会对不上边。
    /// </summary>
    void PlaceOverVirtualScreen()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, _screen.Left, _screen.Top,
            _screen.Right - _screen.Left, _screen.Bottom - _screen.Top,
            Win32.SWP_SHOWWINDOW);
    }

    /// <summary>从大图里裁一块。越界自动收进去。</summary>
    internal static CapturedImage? CropPixels(CapturedImage src, int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, Math.Max(0, src.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, src.Height - 1));
        w = Math.Min(w, src.Width - x);
        h = Math.Min(h, src.Height - y);
        if (w <= 0 || h <= 0) return null;

        var dst = new byte[w * 4 * h];
        for (var row = 0; row < h; row++)
            Array.Copy(src.Pixels, (y + row) * src.Stride + x * 4, dst, row * w * 4, w * 4);
        return new CapturedImage(w, h, dst);
    }

    /// <summary>
    /// 单独造一层选区来渲染，不弹窗、不接鼠标——给自测截图用。
    /// 这层蒙层平时盖满整个屏幕，只能靠渲染成图才看得出压暗深浅、提示字大小、
    /// 尺寸标签摆没摆对，断言一个都测不到。
    /// selection 传 null 就是刚弹出来还没拖的样子。
    /// </summary>
    internal static FrameworkElement LayerForShot(CapturedImage shot, Size size, Rect? selection,
                                                  CaptureTool tool = CaptureTool.None,
                                                  Action<CaptureSelectionLayer>? draw = null)
    {
        var layer = new CaptureSelectionLayer(shot) { Tool = tool };
        layer.Measure(size);
        layer.Arrange(new Rect(size));
        if (selection is { } sel) layer.PresetSelection(sel);
        draw?.Invoke(layer);
        layer.UpdateLayout();
        return layer;
    }

    static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Color.FromRgb(0xFF, 0x3B, 0x30); }
    }
}
