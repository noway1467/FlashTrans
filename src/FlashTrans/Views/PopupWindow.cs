using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>划词 / 快捷键弹出的轻量翻译窗：只读结果，可切源、可展开到主窗口。</summary>
public sealed partial class PopupWindow : Window
{
    readonly AppHost _host;
    readonly ResultView _result = new();
    readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal };
    readonly List<(ToggleButton Btn, string? ProviderId)> _tabs = [];
    readonly TextBlock _status = new();
    readonly ToggleButton _pin;
    readonly TextBlock _langLabel = new();

    string _text = "";
    string? _activeProviderId;
    bool _aggregate;
    bool _userResized;
    bool _widthPinned;
    bool _applyingWidth;
    CancellationTokenSource? _cts;
    TranslateBatch? _batch;

    static AppSettings S => SettingsService.Instance.Current;
    static TranslateEngine Engine => TranslateEngine.Instance;

    public PopupWindow(AppHost host)
    {
        _host = host;

        Title = "闪译";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        MinWidth = 260;
        MinHeight = 120;
        Width = S.PopupWidth;
        MaxHeight = S.PopupMaxHeight;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;
        Topmost = true;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false,
        });

        _pin = PillToggle("钉住", S.PopupCloseOnBlur == false, on =>
        {
            S.PopupCloseOnBlur = !on;
            SettingsService.Instance.Touch();
        });
        _pin.ToolTip = "钉住后失去焦点也不关闭";

        Content = BuildLayout();

        PreviewKeyDown += OnKey;
        Deactivated += (_, _) =>
        {
            if (S.PopupCloseOnBlur && _pin.IsChecked != true) HidePopup();
        };
        SizeChanged += (_, e) =>
        {
            if (!IsVisible) return;
            // 宽度一直是手动的，所以拖宽就算用户调过；
            // 高度只在关掉自适应后才算，否则内容变化也会被误判成手动调整。
            if (e.WidthChanged && !_applyingWidth)
            {
                _widthPinned = true;
                // 高度自适应时，WPF 每轮布局都拿 Width 反过来压窗口。
                // 不把拖出来的实际宽度写回 Width，下一轮就会弹回旧值。
                if (SizeToContent != SizeToContent.Manual && ActualWidth > 0 && Width != ActualWidth)
                {
                    _applyingWidth = true;
                    Width = ActualWidth;
                    _applyingWidth = false;
                }
            }
            if (e.HeightChanged && SizeToContent == SizeToContent.Manual) _userResized = true;
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW);
        };
    }

    // ------------------------------------------------------------- 布局

    UIElement BuildLayout()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 顶栏
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 源标签
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 状态栏

        grid.Children.Add(BuildHeader());
        grid.Children.Add(BuildTabs());

        var resultBox = new Border { Margin = new Thickness(8, 6, 8, 0), Child = _result };
        UiKit.SetGrid(resultBox, row: 2);
        grid.Children.Add(resultBox);

        grid.Children.Add(BuildStatusBar());

        var shell = new Border { BorderThickness = new Thickness(1), Child = grid };
        shell.SetResourceReference(Border.BorderBrushProperty, "Border");
        return shell;
    }

    UIElement BuildHeader()
    {
        var bar = new Grid { Margin = new Thickness(7, 5, 7, 5) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _langLabel.FontSize = 11.5;
        _langLabel.VerticalAlignment = VerticalAlignment.Center;
        _langLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        _langLabel.Cursor = Cursors.Hand;
        _langLabel.ToolTip = "点击切换目标语言";
        _langLabel.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowLangMenu(); };

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(UiKit.Icon(UiKit.IconGlobe, 13));
        var pad = new Border { Width = 6 };
        left.Children.Add(pad);
        left.Children.Add(_langLabel);
        UiKit.SetGrid(left, col: 0);
        bar.Children.Add(left);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        tools.Children.Add(_pin);
        tools.Children.Add(UiKit.IconButton(UiKit.IconCopy, "复制译文", (_, _) => CopyResult(), 12));
        tools.Children.Add(UiKit.IconButton(UiKit.IconRefresh, "重新翻译", (_, _) =>
        {
            // 只清当前这段，别把别人的缓存一起端掉
            Engine.Cache.InvalidateText(_text);
            Run();
        }, 12));
        tools.Children.Add(UiKit.IconButton(UiKit.IconExpand, "在主窗口中打开", (_, _) => _host.ExpandToMain(_text), 12));
        tools.Children.Add(UiKit.IconButton(UiKit.IconClose, "关闭（Esc）", (_, _) => HidePopup(), 12));
        UiKit.SetGrid(tools, col: 1);
        bar.Children.Add(tools);

        var header = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar,
        };
        header.SetResourceReference(Border.BackgroundProperty, "BgAlt");
        header.SetResourceReference(Border.BorderBrushProperty, "Border");
        header.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { /* 忽略 */ } };
        UiKit.SetGrid(header, row: 0);
        return header;
    }

    UIElement BuildTabs()
    {
        var sv = new ScrollViewer
        {
            Content = _tabStrip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(8, 7, 8, 0),
        };
        sv.PreviewMouseWheel += (s, e) =>
        {
            if (s is ScrollViewer v) v.ScrollToHorizontalOffset(v.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        UiKit.SetGrid(sv, row: 1);
        _tabsHost = sv;
        return sv;
    }

    ScrollViewer _tabsHost = null!;

    UIElement BuildStatusBar()
    {
        _status.FontSize = 11;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_status);

        var dict = new Button { Content = "词典", FontSize = 11, Height = 22, Padding = new Thickness(8, 0, 8, 0) };
        dict.SetResourceReference(StyleProperty, "GhostBtn");
        dict.ToolTip = "在欧路词典中查询";
        dict.Click += (_, _) => Lookup(_text);
        UiKit.SetGrid(dict, col: 1);
        grid.Children.Add(dict);
        _dictBtn = dict;

        var bar = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(9, 3, 6, 3),
            Margin = new Thickness(0, 6, 0, 0),
            Child = grid,
        };
        bar.SetResourceReference(Border.BackgroundProperty, "BgAlt");
        bar.SetResourceReference(Border.BorderBrushProperty, "Border");
        UiKit.SetGrid(bar, row: 3);
        return bar;
    }

    Button _dictBtn = null!;

    ToggleButton PillToggle(string text, bool isChecked, Action<bool> onChange)
    {
        var btn = new ToggleButton
        {
            Content = text,
            IsChecked = isChecked,
            FontSize = 11,
            Height = 22,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(0, 0, 4, 0),
            Focusable = false,
        };
        btn.SetResourceReference(StyleProperty, "ProviderTab");
        btn.Checked += (_, _) => onChange(true);
        btn.Unchecked += (_, _) => onChange(false);
        return btn;
    }
}
