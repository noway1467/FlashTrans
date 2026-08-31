using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

public partial class MainWindow : Window
{
    readonly AppHost _host;
    readonly ResultView _result = new();
    readonly DispatcherTimer _debounce;

    LangPicker _from = null!;
    LangPicker _to = null!;
    Button _multiBtn = null!;
    ToggleButton _bilingualBtn = null!;
    ToggleButton _pinBtn = null!;

    readonly List<(ToggleButton Btn, string? ProviderId)> _tabs = [];
    string? _activeProviderId;      // null 表示聚合标签
    bool _aggregateSelected;

    CancellationTokenSource? _cts;
    TranslateBatch? _batch;
    bool _suppressInput;
    string? _settingsSignature;

    static AppSettings S => SettingsService.Instance.Current;
    static TranslateEngine Engine => TranslateEngine.Instance;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        // 单文件发布里 Pack URI 图标可能退回内容包解析并拿不到宿主路径，直接用旁边的 ico 更稳。
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (System.IO.File.Exists(iconPath))
        {
            try { Icon = BitmapFrame.Create(new Uri(iconPath)); }
            catch { /* 图标失败不影响主窗可用，托盘还有自己的加载路径。 */ }
        }

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(S.TypeDelayMs) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Translate(); };

        ResultHost.Content = _result;
        _result.CopyRequested += CopyToClipboard;
        _result.LookupRequested += LookupInEudic;

        BuildHeader();
        RestoreGeometry();
        ApplySettings();
        RebuildTabs();
        _result.ShowMessage("输入或粘贴文本，回车开始翻译");
        UpdateStatus();
        _settingsSignature = SettingsSignature();

        PreviewKeyDown += OnWindowKey;
        Deactivated += (_, _) => { if (!S.AlwaysOnTop) PersistGeometry(); };
    }

    // ------------------------------------------------------------- 顶栏

    void BuildHeader()
    {
        _from = new LangPicker(includeAuto: true) { SelectedCode = S.SourceLang };
        _from.SelectionChanged += code =>
        {
            S.SourceLang = code;
            SettingsService.Instance.Touch();
            Translate();
        };

        var swap = UiKit.IconButton(UiKit.IconSwap, "交换语言（Ctrl+Shift+S）", (_, _) => SwapLanguages());

        _to = new LangPicker { SelectedCode = S.TargetLang };
        _to.SelectionChanged += code =>
        {
            S.TargetLang = code;
            if (S.MultiTargetEnabled && !S.MultiTargets.Contains(code, StringComparer.OrdinalIgnoreCase))
                S.MultiTargets.Insert(0, code);
            SettingsService.Instance.Touch();
            UpdateMultiButton();
            Translate();
        };

        LangHost.Children.Add(_from);
        LangHost.Children.Add(swap);
        LangHost.Children.Add(_to);

        _multiBtn = new Button { Content = "多语言", FontSize = 11.5, Focusable = false };
        _multiBtn.SetResourceReference(StyleProperty, "GhostBtn");
        _multiBtn.ToolTip = "同时翻译成多个语言";
        _multiBtn.Click += (_, _) => MultiLangPopup.Show(_multiBtn, () =>
        {
            UpdateMultiButton();
            Translate();
        });
        LangHost.Children.Add(_multiBtn);
        UpdateMultiButton();

        _bilingualBtn = Toggle("双语对照", S.Bilingual, on =>
        {
            S.Bilingual = on;
            SettingsService.Instance.Touch();
            Rerender();
        });
        _pinBtn = Toggle("置顶", S.AlwaysOnTop, on =>
        {
            S.AlwaysOnTop = on;
            Topmost = on;
            SettingsService.Instance.Touch();
        });

        ToolHost.Children.Add(_bilingualBtn);
        ToolHost.Children.Add(_pinBtn);
        ToolHost.Children.Add(UiKit.IconButton(UiKit.IconSettings, "设置", (_, _) => _host.ShowSettings()));
        ToolHost.Children.Add(UiKit.IconButton(UiKit.IconMinimize, "最小化",
            (_, _) => WindowState = WindowState.Minimized));
        ToolHost.Children.Add(UiKit.IconButton(UiKit.IconClose, "关闭（回到托盘）", (_, _) => Close()));

        // 输入区左侧工具
        InputTools.Children.Add(UiKit.IconButton(UiKit.IconTrash, "清空", (_, _) =>
        {
            Input.Clear();
            _result.ShowMessage("输入或粘贴文本，回车开始翻译");
            Input.Focus();
        }, 12));
        InputTools.Children.Add(UiKit.IconButton(UiKit.IconCopy, "复制全部译文", (_, _) =>
        {
            if (_batch is not null) CopyToClipboard(ResultView.AllText(_batch));
        }, 12));
        InputTools.Children.Add(UiKit.IconButton(UiKit.IconRefresh, "重新翻译（忽略缓存）", (_, _) =>
        {
            // 只清当前这段，别把别人的缓存一起端掉
            Engine.Cache.InvalidateText(Input.Text.Trim());
            Translate(force: true);
        }, 12));
    }

    ToggleButton Toggle(string text, bool isChecked, Action<bool> onChange)
    {
        var btn = new ToggleButton
        {
            Content = text,
            IsChecked = isChecked,
            FontSize = 11.5,
            Height = 25,
            Padding = new Thickness(9, 0, 9, 0),
            Margin = new Thickness(0, 0, 3, 0),
            Focusable = false,
        };
        btn.SetResourceReference(StyleProperty, "ProviderTab");
        btn.Checked += (_, _) => onChange(true);
        btn.Unchecked += (_, _) => onChange(false);
        return btn;
    }

    void UpdateMultiButton()
    {
        var on = S.MultiTargetEnabled && S.MultiTargets.Count > 0;
        _multiBtn.Content = on ? $"多语言 {S.MultiTargets.Count}" : "多语言";
        _multiBtn.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        _multiBtn.SetResourceReference(ForegroundProperty, on ? "Accent" : "TextDim");
        _to.IsEnabled = !on;
    }

    void SwapLanguages()
    {
        var from = S.SourceLang == Languages.Auto
            ? LangDetect.Guess(Input.Text.Length > 0 ? Input.Text : "hello")
            : S.SourceLang;
        var to = S.TargetLang;
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;

        S.SourceLang = to;
        S.TargetLang = from;
        _from.SelectedCode = to;
        _to.SelectedCode = from;
        SettingsService.Instance.Touch();

        // 顺手把译文换到输入框，方便反向确认
        if (_batch is not null && _batch.Results.FirstOrDefault(r => r.Ok)?.Get(to) is { } translated)
            SetInput(translated, translate: true);
        else Translate();
    }
}
