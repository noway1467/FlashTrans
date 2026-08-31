using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using FlashTrans.Core;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>设置窗口：左侧分类，右侧表单。全部代码构建，避免额外 XAML 解析开销。</summary>
public sealed partial class SettingsWindow : Window
{
    readonly AppHost _host;
    readonly ContentControl _pageHost = new();
    readonly StackPanel _navPanel = new();
    readonly List<(string Key, ToggleButton Btn, Func<UIElement> Build)> _pages = [];
    readonly TextBlock _footer = new();

    static AppSettings S => SettingsService.Instance.Current;
    static TranslateEngine Engine => TranslateEngine.Instance;

    public SettingsWindow(AppHost host)
    {
        _host = host;

        Title = "闪译 · 设置";
        Width = 720;
        Height = 560;
        MinWidth = 620;
        MinHeight = 440;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        FontSize = 13;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false,
        });

        _pages.Add(("general", NavButton("通用"), BuildGeneralPage));
        _pages.Add(("sources", NavButton("翻译源"), BuildSourcesPage));
        _pages.Add(("languages", NavButton("语言"), BuildLanguagesPage));
        _pages.Add(("capture", NavButton("截图"), BuildCapturePage));
        _pages.Add(("hotkeys", NavButton("快捷键"), BuildHotkeyPage));
        _pages.Add(("appearance", NavButton("外观与词典"), BuildAppearancePage));
        _pages.Add(("about", NavButton("关于"), BuildAboutPage));

        Content = BuildShell();
        SelectTab("general");

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
        Closed += (_, _) => SettingsService.Instance.Touch();
    }

    /// <summary>切换到指定分类（"general" / "sources" / "languages" / "capture" / "hotkeys" / "appearance"）。</summary>
    public void SelectTab(string key)
    {
        var page = _pages.FirstOrDefault(p => p.Key == key);
        if (page.Btn is null) page = _pages[0];

        foreach (var (_, btn, _) in _pages) btn.IsChecked = ReferenceEquals(btn, page.Btn);
        _pageHost.Content = page.Build();
    }

    // ------------------------------------------------------------- 外壳

    UIElement BuildShell()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(BuildTitleBar());

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _navPanel.Margin = new Thickness(8, 10, 8, 10);
        foreach (var (_, btn, _) in _pages) _navPanel.Children.Add(btn);

        var nav = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer
            {
                Content = _navPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };
        nav.SetResourceReference(Border.BackgroundProperty, "BgAlt");
        nav.SetResourceReference(Border.BorderBrushProperty, "Border");
        UiKit.SetGrid(nav, col: 0);
        body.Children.Add(nav);

        var scroll = new ScrollViewer
        {
            Content = _pageHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(16, 14, 14, 16),
        };
        scroll.SetResourceReference(StyleProperty, "PlainScrollViewer");
        UiKit.SetGrid(scroll, col: 1);
        body.Children.Add(scroll);

        UiKit.SetGrid(body, row: 1);
        grid.Children.Add(body);
        grid.Children.Add(BuildFooter());

        var shell = new Border { BorderThickness = new Thickness(1), Child = grid };
        shell.SetResourceReference(Border.BorderBrushProperty, "Border");
        return shell;
    }

    UIElement BuildTitleBar()
    {
        var row = new Grid { Margin = new Thickness(12, 0, 6, 0), Height = 38 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = UiKit.Text("设置", 13, "Text", FontWeights.SemiBold);
        UiKit.SetGrid(title, col: 0);
        row.Children.Add(title);

        var tools = UiKit.Row(2,
            UiKit.IconButton(UiKit.IconClose, "关闭（Esc）", (_, _) => Close(), 12));
        tools.HorizontalAlignment = HorizontalAlignment.Right;
        UiKit.SetGrid(tools, col: 1);
        row.Children.Add(tools);

        var bar = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
        bar.SetResourceReference(Border.BackgroundProperty, "BgAlt");
        bar.SetResourceReference(Border.BorderBrushProperty, "Border");
        bar.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { /* 忽略 */ } };
        UiKit.SetGrid(bar, row: 0);
        return bar;
    }

    UIElement BuildFooter()
    {
        _footer.FontSize = 11;
        _footer.VerticalAlignment = VerticalAlignment.Center;
        _footer.TextTrimming = TextTrimming.CharacterEllipsis;
        _footer.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
        _footer.Text = "改动即时生效，自动保存到 settings.json";

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_footer);

        var open = new Button { Content = "打开配置目录", FontSize = 11.5, Padding = new Thickness(10, 3, 10, 3) };
        open.SetResourceReference(StyleProperty, "GhostBtn");
        open.Click += (_, _) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(SettingsService.Instance.ConfigDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    SettingsService.Instance.ConfigDir) { UseShellExecute = true });
            }
            catch (Exception ex) { Toast("打开失败：" + ex.Message); }
        };

        var done = new Button { Content = "完成", FontSize = 12, Padding = new Thickness(18, 4, 18, 4) };
        done.SetResourceReference(StyleProperty, "PrimaryBtn");
        done.Click += (_, _) => Close();

        var tools = UiKit.Row(8, open, done);
        tools.HorizontalAlignment = HorizontalAlignment.Right;
        UiKit.SetGrid(tools, col: 1);
        grid.Children.Add(tools);

        var bar = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 7, 10, 7),
            Child = grid,
        };
        bar.SetResourceReference(Border.BackgroundProperty, "BgAlt");
        bar.SetResourceReference(Border.BorderBrushProperty, "Border");
        UiKit.SetGrid(bar, row: 2);
        return bar;
    }

    ToggleButton NavButton(string label)
    {
        var btn = new ToggleButton
        {
            Content = label,
            Height = 30,
            Margin = new Thickness(0, 0, 0, 3),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 0, 6, 0),
            FontSize = 12.5,
            Focusable = false,
        };
        btn.SetResourceReference(StyleProperty, "ProviderTab");
        btn.PreviewMouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            var key = _pages.First(p => ReferenceEquals(p.Btn, s)).Key;
            SelectTab(key);
        };
        return btn;
    }

    void Toast(string message)
    {
        _footer.Text = message;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_footer.Text == message) _footer.Text = "改动即时生效，自动保存到 settings.json";
        };
        timer.Start();
    }

    static void Save() => SettingsService.Instance.Touch();
}
