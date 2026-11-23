using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

/// <summary>
/// 设置本身的自测，回答的是「我在设置里改的东西，到底存下来没有、生效没有」。
///
/// 起因：开机自启勾了却不启动。真实原因是 Run 项里存的绝对路径在程序换目录之后就废了，
/// 而判断「开着没有」只看那条命令里含不含 FlashTrans 这个词，于是设置里永远显示开着。
/// </summary>
static class SettingsProbe
{
    public static void RunUi(Action<string, Action> step, string? preview = null)
    {
        step("设置：深浅主题与最小窗口下说明最多两行", () => DescriptionLayout(preview));
        step("设置：长截图录键、冲突拒绝、控件交互与保存", () => LongShotSettingsUi(preview));
        step("设置：OCR 控件切页、最小窗口与配置保存", UiProbe.OcrSettingsPageProbe);
        step("设置：系统 OCR 语言包状态、安装命令与系统设置入口", OcrLanguagePacks);
        step("主窗口：输入提示随内容显隐，空白时列出当前全局快捷键", () => MainWindowWelcome(preview));
        step("设置：标注色板深浅主题、自选色保存与重新打开", () => ColorSetting(preview));
        step("色板：HEX 校验与 HSV 边界", () =>
        {
            foreach (var value in new[] { "#000000", "#FFFFFF", "#18a7b5", "123456" })
                if (!AnnotationColorPalette.TryParseHex(value, out var color) || color.A != 255)
                    throw new InvalidOperationException("合法颜色未识别：" + value);
            foreach (var value in new[] { "", "#FFF", "#12345G", "##123456", "#00112233" })
                if (AnnotationColorPalette.TryParseHex(value, out _)) throw new InvalidOperationException("接受了非法颜色：" + value);
            if (AnnotationColorPalette.FromHsv(360, 1, 1) != Colors.Red
                || AnnotationColorPalette.FromHsv(120, 1, 1) != Colors.Lime
                || AnnotationColorPalette.FromHsv(240, 1, 1) != Colors.Blue
                || AnnotationColorPalette.FromHsv(180, 0, 1) != Colors.White
                || AnnotationColorPalette.FromHsv(180, 1, 0) != Colors.Black)
                throw new InvalidOperationException("HSV 边界颜色错误");
        });
    }

    static void ColorSetting(string? preview)
    {
        var original = SettingsService.Instance.Current;
        try
        {
            SettingsService.Instance.Apply(AppSettings.CreateDefault());
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeService.ApplyTheme(theme);
                var window = new SettingsWindow(new AppHost());
                try
                {
                    window.SelectTab("capture"); window.Show(); window.UpdateLayout(); Pump();
                    var button = Descendants<Button>(window).Single(b => b.Name == "CaptureColorSetting");
                    button.BringIntoView(); Pump();
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    var palette = PresentationSource.CurrentSources.Cast<PresentationSource>()
                        .Where(s => s.RootVisual is not null).SelectMany(s => Descendants<AnnotationColorPalette>(s.RootVisual)).Single();
                    var hex = Descendants<TextBox>(palette).Single();
                    hex.Text = "#37B9A6";
                    if (Descendants<Button>(palette).Any(b => b.Name == "ApplyColor"))
                        throw new InvalidOperationException("色板仍要求二次确认");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    if (SettingsService.Instance.Current.CapturePenColor != "#37B9A6")
                        throw new InvalidOperationException("设置页未应用自选颜色");
                    SettingsService.Instance.Load();
                    window.SelectTab("ocr"); window.SelectTab("capture"); Pump();
                    if (!Descendants<TextBlock>(window).Any(t => t.Text == "#37B9A6"))
                        throw new InvalidOperationException("切页或读取配置丢失颜色");
                    button = Descendants<Button>(window).Single(b => b.Name == "CaptureColorSetting");
                    button.BringIntoView(); Pump();
                    if (preview is not null) Snapshot(window, System.IO.Path.Combine(preview, $"settings-color-{theme}.png"));
                }
                finally { window.Close(); }
            }
        }
        finally { SettingsService.Instance.Apply(original); ThemeService.Apply(original); }
    }

    static void DescriptionLayout(string? preview)
    {
        var original = SettingsService.Instance.Current;
        try
        {
            var settings = AppSettings.CreateDefault();
            settings.QuickLaunchItems = [new() { Name = "Visual Studio Code", Path = @"C:\Apps\Code.exe" },
                new() { Name = "项目资料", Path = @"D:\Workspace\FlashTrans", IsFolder = true }];
            SettingsService.Instance.Apply(settings);
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var width in new[] { 620d, 720d })
            {
                ThemeService.ApplyTheme(theme);
                var window = new SettingsWindow(new AppHost()) { Width = width, Height = 560, ShowActivated = false };
                try
                {
                    window.Show();
                    foreach (var tab in new[] { "general", "sources", "languages", "capture", "ocr", "quicklaunch", "desktopshelf", "hotkeys", "appearance", "about" })
                    {
                        window.SelectTab(tab); Pump(); window.UpdateLayout();
                        foreach (var text in Descendants<TextBlock>(window).Where(t => Equals(t.Tag, "SettingsDescription")))
                        {
                            if (text.ActualHeight > 32.1 || text.MaxHeight != 32 || text.ActualWidth <= 0)
                                throw new InvalidOperationException($"{tab} 说明布局越界：{text.Text}");
                            // 不靠省略号伪装精简：常规页面文案在最窄窗口也必须完整显示。
                            if (tab == "about" && text.Text.StartsWith("配置文件：")) continue;
                            var full = new TextBlock { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize,
                                TextWrapping = TextWrapping.Wrap, LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
                            full.Measure(new Size(text.ActualWidth, double.PositiveInfinity));
                            if (full.DesiredSize.Height > 32.1)
                                throw new InvalidOperationException($"{tab} 说明仍超过两行（宽 {text.ActualWidth}）：{text.Text}");
                        }
                        if (preview is not null && tab is "capture" or "ocr" or "quicklaunch")
                            Snapshot(window, System.IO.Path.Combine(preview, $"settings-{tab}-{theme}-{width}.png"));
                    }
                }
                finally { window.Close(); }
            }
        }
        finally { SettingsService.Instance.Apply(original); ThemeService.Apply(original); }
    }

    static void LongShotSettingsUi(string? preview)
    {
        var original = SettingsService.Instance.Current;
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                SettingsService.Instance.Apply(AppSettings.CreateDefault());
                ThemeService.ApplyTheme(theme);
                var window = new SettingsWindow(new AppHost()) { Width = 620, Height = 560, ShowInTaskbar = false };
                try
                {
                    window.SelectTab("capture"); window.Show(); window.Activate(); Pump(); window.UpdateLayout();
                    if (!Descendants<TextBlock>(window).Any(t => t.Text == "长截图"))
                        throw new InvalidOperationException("截图页缺少独立长截图分组");
                    var box = Descendants<TextBox>(window).Single(t => t.Name == nameof(AppSettings.LongShotToggleHotkey));
                    var auto = Descendants<ComboBox>(window).Single(t => t.Name == nameof(AppSettings.LongShotAutoScroll));
                    var interval = Descendants<Slider>(window).Single(t => t.Name == nameof(AppSettings.LongShotScrollIntervalMs));
                    var maxHeight = Descendants<Slider>(window).Single(t => t.Name == nameof(AppSettings.LongShotMaxHeight));
                    var live = Descendants<CheckBox>(window).Single(t => t.Name == nameof(AppSettings.LongShotLivePreview));
                    var show = Descendants<CheckBox>(window).Single(t => t.Name == nameof(AppSettings.LongShotShowPreview));
                    var fit = Descendants<ComboBox>(window).Single(t => t.Name == nameof(AppSettings.LongShotPreviewFitWidth));
                    if (interval.Minimum != LongShotService.MinScrollIntervalMs || interval.Maximum != LongShotService.MaxScrollIntervalMs
                        || interval.TickFrequency != 20 || maxHeight.Minimum != LongShotService.MinMaxHeight
                        || maxHeight.Maximum != LongShotService.MaxMaxHeight || maxHeight.TickFrequency != 2000)
                        throw new InvalidOperationException("长截图设置控件与引擎边界不一致");

                    auto.SelectedIndex = 1;
                    interval.Value = 240;
                    maxHeight.Value = 18000;
                    interval.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                    maxHeight.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                    live.IsChecked = false;
                    show.IsChecked = false;
                    fit.SelectedIndex = 1;
                    box.BringIntoView(); Pump(); box.Focus(); Keyboard.Focus(box); Pump();
                    if (!window.TryRecordHotkey(HotkeySpec.Parse("Ctrl+Shift+F8")) || box.Text != "Ctrl+Shift+F8")
                        throw new InvalidOperationException("长截图录键框未接管已注册全局键的录制入口");
                    foreach (var rejected in new[] { "F1", RecordHud.PauseHotkey, RecordHud.CancelHotkey, "A" })
                    {
                        if (!window.TryRecordHotkey(HotkeySpec.Parse(rejected)) || box.Text != "Ctrl+Shift+F8"
                            || SettingsService.Instance.Current.LongShotToggleHotkey != "Ctrl+Shift+F8")
                            throw new InvalidOperationException("冲突或非法键覆盖了原长截图快捷键：" + rejected);
                    }
                    Press(Key.Escape);
                    if (!window.IsVisible || box.Text != "Ctrl+Shift+F8")
                        throw new InvalidOperationException("录制 Esc 关闭了设置窗口或覆盖了快捷键");
                    Press(Key.Back);
                    if (box.Text != "" || SettingsService.Instance.Current.LongShotToggleHotkey != "")
                        throw new InvalidOperationException("Backspace 没有关闭长截图快捷键");
                    window.TryRecordHotkey(HotkeySpec.Parse("F8"));
                    Press(Key.Delete);
                    if (box.Text != "") throw new InvalidOperationException("Delete 没有清空长截图快捷键");
                    window.TryRecordHotkey(HotkeySpec.Parse("F12"));

                    foreach (var control in new FrameworkElement[] { box, auto, interval, maxHeight, live, show, fit })
                    {
                        control.BringIntoView(); Pump(); window.UpdateLayout();
                        var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
                        if (bounds.Right > window.ActualWidth - 8 || bounds.Left < 138 || control.ActualHeight <= 0)
                            throw new InvalidOperationException("620px 长截图设置控件溢出：" + control.Name);
                    }
                    SettingsService.Instance.Load();
                    var saved = SettingsService.Instance.Current;
                    if (saved.LongShotToggleHotkey != "F12" || !saved.LongShotAutoScroll || saved.LongShotScrollIntervalMs != 240
                        || saved.LongShotMaxHeight != 18000 || saved.LongShotLivePreview || saved.LongShotShowPreview || saved.LongShotPreviewFitWidth)
                        throw new InvalidOperationException("长截图控件交互没有全部写入隔离配置");
                    window.SelectTab("ocr"); window.SelectTab("capture"); Pump(); window.UpdateLayout();
                    box = Descendants<TextBox>(window).Single(t => t.Name == nameof(AppSettings.LongShotToggleHotkey));
                    if (box.Text != "F12" || Descendants<Slider>(window).Single(t => t.Name == nameof(AppSettings.LongShotScrollIntervalMs)).Value != 240)
                        throw new InvalidOperationException("切页后长截图控件未恢复保存的值");
                    box.BringIntoView(); Pump();
                    if (preview is not null) Snapshot(window, System.IO.Path.Combine(preview, $"settings-longshot-{theme}-620.png"));

                    void Press(Key key) => box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(box)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }
                finally { window.Close(); }
            }
        }
        finally { SettingsService.Instance.Apply(original); ThemeService.Apply(original); }
    }

    static void OcrLanguagePacks()
    {
        if (!SettingsWindow.OcrLanguageInstalled(["en-US", "ko"], "ko-KR") || SettingsWindow.OcrLanguageInstalled(["en-US", "zh-Hans-CN"], "ru-RU")
            || !SettingsWindow.OcrLanguageInstalled(["zh-Hans-CN"], "zh-CN"))
            throw new InvalidOperationException("语言包按主语言比对错误");
        var command = SettingsWindow.OcrInstallCommand("ru-RU");
        if (!command.Contains("'Language.OCR*ru-RU*'") || !command.EndsWith("Add-WindowsCapability -Online"))
            throw new InvalidOperationException("安装命令错误：" + command);
        var w = new SettingsWindow(new AppHost()) { Width = 620, Height = 440, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            w.SelectTab("ocr"); w.Show(); w.UpdateLayout(); Pump();
            var texts = Descendants<TextBlock>(w).Select(t => t.Text).ToList();
            if (!texts.Contains("系统 OCR 语言包") || !texts.Contains("韩语") || !texts.Contains("俄语"))
                throw new InvalidOperationException("OCR 页缺少语言包分组");
            var missing = SettingsWindow.SuggestedOcrLanguages.Count(l => !SettingsWindow.OcrLanguageInstalled(OcrService.AvailableLanguages, l.Tag));
            var copyButtons = Descendants<Button>(w).Count(b => Equals(b.Content, "复制安装命令"));
            if (copyButtons != missing) throw new InvalidOperationException($"复制命令按钮数 {copyButtons} 与未安装语言数 {missing} 不一致");
            if (!Descendants<Button>(w).Any(b => Equals(b.Content, "打开 Windows 语言设置")))
                throw new InvalidOperationException("缺少系统语言设置入口");
            var installedChips = OcrService.AvailableLanguages.Select(OcrService.DisplayName).ToList();
            if (installedChips.Any(name => !texts.Contains(name))) throw new InvalidOperationException("已安装语言没有全部列出");
        }
        finally { w.Close(); }
    }

    static void MainWindowWelcome(string? preview)
    {
        var s = SettingsService.Instance.Current;
        var typeTranslate = s.TranslateOnType;
        // 关掉输入即译，避免往输入框里写字时真的去请求翻译服务。
        s.TranslateOnType = false;
        var main = new MainWindow(new AppHost()) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            main.Show(); main.UpdateLayout(); Pump();
            var hint = (TextBlock)main.FindName("InputHint");
            var input = (TextBox)main.FindName("Input");
            if (hint.Visibility != Visibility.Visible || !hint.Text.Contains("翻译")) throw new InvalidOperationException("空输入没有提示文字");
            var texts = Descendants<TextBlock>(main).Select(t => t.Text).ToList();
            foreach (var hotkey in new[] { s.HkTranslateSelection, s.HkCaptureOcr, s.HkTranslateClipboard, s.QuickLaunchEnabled ? s.HkQuickLaunch : "" }.Where(h => h.Length > 0))
                if (!texts.Contains(Interop.HotkeySpec.Parse(hotkey).ToString()))
                    throw new InvalidOperationException("空白指引缺少快捷键：" + hotkey);
            if (preview is not null)
                foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
                {
                    ThemeService.ApplyTheme(theme); main.UpdateLayout(); Pump();
                    Snapshot(main, System.IO.Path.Combine(preview, $"main-welcome-{theme}.png"));
                }
            input.Text = "hello";
            if (hint.Visibility != Visibility.Collapsed) throw new InvalidOperationException("输入后提示没有隐藏");
            input.Clear();
            if (hint.Visibility != Visibility.Visible) throw new InvalidOperationException("清空后提示没有恢复");
        }
        finally { main.Close(); s.TranslateOnType = typeTranslate; }
    }

    static void Snapshot(Window window, string path)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path); png.Save(stream);
    }

    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    public static void RunAll(Action<string, Action> step)
    {
        step("设置：每个字段都能存下来再读回来", RoundTrip);
        step("设置：旧配置默认单列，新视图选择可以保存", () =>
        {
            var old = JsonSerializer.Deserialize("{}", SettingsJson.Default.AppSettings)!;
            if (old.MultiColumnResults) throw new InvalidOperationException("旧配置意外启用了多列");
            old.MultiColumnResults = true;
            var json = JsonSerializer.Serialize(old, SettingsJson.Default.AppSettings);
            if (!JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings)!.MultiColumnResults)
                throw new InvalidOperationException("多列设置没有往返保留");
        });
        step("设置：算出来的字段不落盘", NoComputedFields);
        step("设置：Normalize 不动设置页里能填到的边界值", BoundsMatchUi);
        step("设置：Normalize 反复跑结果不变", NormalizeIsStable);
        step("设置：v17→v18 长截图默认值、旧热键让位与幂等迁移", LongShotMigration);
        step("设置：长截图快捷键校验与规范化", LongShotHotkeyNormalization);
        step("设置：长截图、全局与录制快捷键双向冲突检测", LongShotHotkeyConflicts);
        step("开机自启：从 Run 项的命令里取出 exe 路径", ParseCommand);
        step("开机自启：程序换了目录要认出来路径已经失效", DetectsMovedExe);
        step("开机自启：自测程序不许去改用户的 Run 项", RefusesFromForeignProcess);
        StartupProbe.RunAll(step);
    }

    /// <summary>
    /// EnabledProviders 这种算出来的属性不该出现在配置文件里：
    /// 它会把整份翻译源（含加密后的密钥）多存一遍，读回来又没人要。
    /// </summary>
    static void NoComputedFields()
    {
        var json = JsonSerializer.Serialize(AppSettings.CreateDefault(), SettingsJson.Default.AppSettings);
        if (json.Contains("enabledProviders", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("enabledProviders 被写进配置文件了");
    }

    // ---------------------------------------------------------------- 存取

    /// <summary>
    /// 把每个字段都改成一个跟默认值不同的值，序列化再读回来，逐个字段比。
    /// 只读属性、漏在源生成器外面的字段，都会在这儿露出来 —— 那意味着
    /// 用户在设置里改了它，重启就白改。
    /// </summary>
    static void RoundTrip()
    {
        var s = AppSettings.CreateDefault();
        var touched = new List<PropertyInfo>();

        foreach (var p in Writable())
        {
            var value = Distinct(p, p.GetValue(s));
            if (value is null) continue;
            p.SetValue(s, value);
            touched.Add(p);
        }
        if (touched.Count < 40)
            throw new InvalidOperationException($"只改到了 {touched.Count} 个字段，探针自己写坏了");

        var json = JsonSerializer.Serialize(s, SettingsJson.Default.AppSettings);
        var back = JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings)
                   ?? throw new InvalidOperationException("读回来是 null");

        var lost = touched.Where(p => !Same(p.GetValue(s), p.GetValue(back)))
                          .Select(p => $"{p.Name}（存 {Show(p.GetValue(s))}，读回 {Show(p.GetValue(back))}）")
                          .ToList();
        if (lost.Count > 0)
            throw new InvalidOperationException("这些字段存不下来：" + string.Join("；", lost));
    }

    static IEnumerable<PropertyInfo> Writable() =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(AppSettings.Version));

    /// <summary>给一个跟现值不同的值。给不出来（比如翻译源列表）就返回 null，跳过。</summary>
    static object? Distinct(PropertyInfo p, object? current)
    {
        var t = p.PropertyType;
        if (t == typeof(bool)) return !(bool)current!;
        if (t == typeof(int)) return (int)current! + 7;
        if (t == typeof(double)) return (double)current! + 1.5;
        if (t == typeof(string)) return (string?)current + "-x";
        if (t == typeof(List<string>)) return new List<string> { "aa", "bb" };
        if (t.IsEnum)
        {
            var values = Enum.GetValues(t).Cast<object>().ToList();
            return values.FirstOrDefault(v => !Equals(v, current)) ?? current;
        }
        return null;   // List<ProviderConfig> 之类，另有用例覆盖
    }

    static bool Same(object? a, object? b)
    {
        if (a is List<string> la && b is List<string> lb) return la.SequenceEqual(lb, StringComparer.Ordinal);
        return Equals(a, b);
    }

    static string Show(object? v) => v is List<string> l ? "[" + string.Join(",", l) + "]" : v?.ToString() ?? "null";

    // ---------------------------------------------------------------- 取值范围

    /// <summary>
    /// 设置页能填到的边界值，Normalize 必须原样放过。
    /// 两边各写一遍数字就会漂：输入框允许 120ms，而 Normalize 夹到 150 —— 用户填的 120
    /// 当场生效，下次启动却变回 150，看起来就是「设置没保存」。
    /// </summary>
    static void BoundsMatchUi()
    {
        Check("输入停顿延迟", AppSettings.MinTypeDelayMs, AppSettings.MaxTypeDelayMs,
            (s, v) => s.TypeDelayMs = v, s => s.TypeDelayMs);
        Check("画笔粗细", (int)CaptureLimits.MinPenWidth, (int)CaptureLimits.MaxPenWidth,
            (s, v) => s.CapturePenWidth = v, s => (int)s.CapturePenWidth);
        Check("文字字号", (int)CaptureLimits.MinFontSize, (int)CaptureLimits.MaxFontSize,
            (s, v) => s.CaptureFontSize = v, s => (int)s.CaptureFontSize);
        Check("马赛克格子", CaptureLimits.MinMosaicBlock, CaptureLimits.MaxMosaicBlock,
            (s, v) => s.CaptureMosaicBlock = v, s => s.CaptureMosaicBlock);
        Check("录制帧率", RecordService.MinFps, RecordService.MaxFps,
            (s, v) => s.RecordFps = v, s => s.RecordFps);
        Check("录制时长", RecordService.MinSeconds, RecordService.MaxSeconds,
            (s, v) => s.RecordMaxSeconds = v, s => s.RecordMaxSeconds);
        Check("自动滚动间隔", LongShotService.MinScrollIntervalMs, LongShotService.MaxScrollIntervalMs,
            (s, v) => s.LongShotScrollIntervalMs = v, s => s.LongShotScrollIntervalMs);
        Check("最大拼接高度", LongShotService.MinMaxHeight, LongShotService.MaxMaxHeight,
            (s, v) => s.LongShotMaxHeight = v, s => s.LongShotMaxHeight);
        foreach (var upper in new[] { false, true })
        {
            var broken = AppSettings.CreateDefault();
            broken.LongShotScrollIntervalMs = upper ? int.MaxValue : int.MinValue;
            broken.LongShotMaxHeight = upper ? int.MaxValue : int.MinValue;
            SettingsService.Normalize(broken);
            if (broken.LongShotScrollIntervalMs != (upper ? LongShotService.MaxScrollIntervalMs : LongShotService.MinScrollIntervalMs)
                || broken.LongShotMaxHeight != (upper ? LongShotService.MaxMaxHeight : LongShotService.MinMaxHeight))
                throw new InvalidOperationException("长截图越界值没有夹回 UI 范围");
        }

        var brokenOcrSize = AppSettings.CreateDefault();
        brokenOcrSize.OcrResultWidth = double.NaN;
        brokenOcrSize.OcrResultHeight = double.PositiveInfinity;
        SettingsService.Normalize(brokenOcrSize);
        if (brokenOcrSize.OcrResultWidth != 520 || brokenOcrSize.OcrResultHeight != 360)
            throw new InvalidOperationException("损坏的 OCR 结果窗口尺寸没有回退到默认值");
        var oldPreview = JsonSerializer.Deserialize("{}", SettingsJson.Default.AppSettings)!;
        if (oldPreview.LongShotPreviewWidth != 720 || oldPreview.LongShotPreviewHeight != 660)
            throw new InvalidOperationException("旧配置缺少长截图窗口尺寸时未使用默认值");
        oldPreview.Version = 15;
        oldPreview.LongShotPreviewWidth = 900;
        if (!SettingsService.Migrate(oldPreview) || oldPreview.Version != AppSettings.CurrentVersion
            || oldPreview.LongShotPreviewWidth != 900 || SettingsService.Migrate(oldPreview))
            throw new InvalidOperationException("长截图尺寸迁移未保持已有宽度或重复迁移");
        oldPreview.LongShotPreviewWidth = double.NaN;
        oldPreview.LongShotPreviewHeight = double.PositiveInfinity;
        SettingsService.Normalize(oldPreview);
        if (oldPreview.LongShotPreviewWidth != 720 || oldPreview.LongShotPreviewHeight != 660)
            throw new InvalidOperationException("损坏的长截图窗口尺寸没有恢复默认值");

        static void Check(string what, int min, int max, Action<AppSettings, int> set, Func<AppSettings, int> get)
        {
            foreach (var v in new[] { min, max })
            {
                var s = AppSettings.CreateDefault();
                set(s, v);
                SettingsService.Normalize(s);
                if (get(s) != v)
                    throw new InvalidOperationException($"{what} 填 {v} 被改成了 {get(s)}");
            }
        }
    }

    static void NormalizeIsStable()
    {
        var s = AppSettings.CreateDefault();
        SettingsService.Normalize(s);
        var once = JsonSerializer.Serialize(s, SettingsJson.Default.AppSettings);
        SettingsService.Normalize(s);
        if (JsonSerializer.Serialize(s, SettingsJson.Default.AppSettings) != once)
            throw new InvalidOperationException("Normalize 跑第二遍又改了东西");
    }

    static void LongShotMigration()
    {
        var old = JsonSerializer.Deserialize("{\"version\":17}", SettingsJson.Default.AppSettings)!;
        if (!SettingsService.Migrate(old) || old.Version != 18 || old.LongShotToggleHotkey != LongShotService.DefaultToggleHotkey
            || old.LongShotAutoScroll || old.LongShotScrollIntervalMs != LongShotService.DefaultScrollIntervalMs
            || old.LongShotMaxHeight != LongShotService.DefaultMaxHeight || !old.LongShotLivePreview
            || !old.LongShotShowPreview || !old.LongShotPreviewFitWidth)
            throw new InvalidOperationException("v17 缺失字段没有补齐长截图默认值");
        var once = JsonSerializer.Serialize(old, SettingsJson.Default.AppSettings);
        if (SettingsService.Migrate(old) || JsonSerializer.Serialize(old, SettingsJson.Default.AppSettings) != once)
            throw new InvalidOperationException("v18 迁移不幂等");

        foreach (var property in typeof(AppSettings).GetProperties().Where(p => p.Name.StartsWith("Hk") && p.PropertyType == typeof(string)))
        {
            var conflict = JsonSerializer.Deserialize("{\"version\":17}", SettingsJson.Default.AppSettings)!;
            conflict.QuickLaunchEnabled = true;
            property.SetValue(conflict, "F2");
            SettingsService.Migrate(conflict);
            if (conflict.LongShotToggleHotkey != "" || !Equals(property.GetValue(conflict), "F2"))
                throw new InvalidOperationException("迁移抢占了旧全局键：" + property.Name);
        }
        foreach (var recordHotkey in new[] { RecordHud.PauseHotkey, RecordHud.CancelHotkey })
        {
            var conflict = new AppSettings { Version = 17, LongShotToggleHotkey = recordHotkey };
            SettingsService.Migrate(conflict);
            if (conflict.LongShotToggleHotkey != "") throw new InvalidOperationException("迁移抢占了录制快捷键");
        }
        var older = new AppSettings { Version = 10, HkQuickLaunch = "F2", QuickLaunchEnabled = true };
        SettingsService.Migrate(older);
        if (older.HkQuickLaunch != "F2" || older.LongShotToggleHotkey != "")
            throw new InvalidOperationException("更早的快速启动迁移误给新增长截图键让位");

        var custom = new AppSettings
        {
            Version = 17, LongShotToggleHotkey = "Ctrl+Shift+F8", LongShotAutoScroll = true,
            LongShotScrollIntervalMs = 240, LongShotMaxHeight = 18000,
            LongShotLivePreview = false, LongShotShowPreview = false, LongShotPreviewFitWidth = false,
        };
        SettingsService.Migrate(custom);
        if (custom.LongShotToggleHotkey != "Ctrl+Shift+F8" || !custom.LongShotAutoScroll || custom.LongShotScrollIntervalMs != 240
            || custom.LongShotMaxHeight != 18000 || custom.LongShotLivePreview || custom.LongShotShowPreview || custom.LongShotPreviewFitWidth)
            throw new InvalidOperationException("迁移覆盖了已经保存的长截图自定义设置");
    }

    static void LongShotHotkeyNormalization()
    {
        foreach (var invalid in new string?[] { null, "", " ", "A", "Ctrl", "Escape", "Ctrl+Escape", "Ctrl+LeftCtrl", "Ctrl+9999", "ImeProcessed" })
        {
            var settings = AppSettings.CreateDefault();
            settings.LongShotToggleHotkey = invalid!;
            SettingsService.Normalize(settings);
            if (settings.LongShotToggleHotkey != "") throw new InvalidOperationException("接受了非法长截图键：" + invalid);
        }
        foreach (var (text, expected) in new[] { ("f1", "F1"), (" F12 ", "F12"),
            ("control + alt + f8", "Ctrl+Alt+F8"), ("shift + win + K", "Shift+Win+K") })
        {
            var settings = AppSettings.CreateDefault();
            settings.LongShotToggleHotkey = text;
            SettingsService.Normalize(settings);
            if (settings.LongShotToggleHotkey != expected) throw new InvalidOperationException("长截图键未规范化：" + text);
        }
    }

    static void LongShotHotkeyConflicts()
    {
        var settings = AppSettings.CreateDefault();
        foreach (var binding in HotkeyManager.Definitions(settings))
        {
            if (HotkeyManager.FindConflict(settings, binding.Action, "F2")?.Contains("长截图开始/暂停") != true)
                throw new InvalidOperationException("全局键没有检查长截图冲突：" + binding.Action);
            if (binding.Text.Length > 0 && HotkeyManager.FindConflict(settings, HotkeyAction.LongShotToggle, binding.Text) is null)
                throw new InvalidOperationException("长截图键没有检查全局冲突：" + binding.Action);
        }
        foreach (var text in new[] { RecordHud.PauseHotkey, RecordHud.CancelHotkey })
            if (HotkeyManager.FindConflict(settings, HotkeyAction.LongShotToggle, text) is null)
                throw new InvalidOperationException("长截图键没有检查录制冲突");
        if (HotkeyManager.FindConflict(settings, HotkeyAction.LongShotToggle, "F2") is not null)
            throw new InvalidOperationException("长截图快捷键与自身冲突");
        settings.LongShotToggleHotkey = "";
        if (HotkeyManager.FindConflict(settings, HotkeyAction.CaptureOcr, "F2") is not null)
            throw new InvalidOperationException("关闭长截图快捷键后仍占用 F2");
        settings.HkQuickLaunch = "Ctrl+Shift+F9";
        if (HotkeyManager.FindConflict(settings, HotkeyAction.LongShotToggle, settings.HkQuickLaunch) is not null)
            throw new InvalidOperationException("未启用的快速启动错误占用快捷键");
        settings.QuickLaunchEnabled = true;
        if (HotkeyManager.FindConflict(settings, HotkeyAction.LongShotToggle, settings.HkQuickLaunch) is null)
            throw new InvalidOperationException("启用快速启动后没有检查长截图冲突");
    }

    // ---------------------------------------------------------------- 开机自启

    static void ParseCommand()
    {
        Same("\"C:\\App\\FlashTrans.exe\" --tray", "C:\\App\\FlashTrans.exe");
        Same("C:\\App\\FlashTrans.exe --tray", "C:\\App\\FlashTrans.exe");
        Same("C:\\App\\FlashTrans.exe", "C:\\App\\FlashTrans.exe");
        if (StartupService.PathOf("") is not null) throw new InvalidOperationException("空命令该给 null");
        if (StartupService.PathOf(null) is not null) throw new InvalidOperationException("null 该给 null");

        static void Same(string command, string want)
        {
            var got = StartupService.PathOf(command);
            if (!string.Equals(got, want, StringComparison.Ordinal))
                throw new InvalidOperationException($"{command} → {got}，应该是 {want}");
        }
    }

    /// <summary>用户碰上的就是这个：升级换了个带版本号的文件夹，Run 项还指着老路径。</summary>
    static void DetectsMovedExe()
    {
        const string now = @"C:\App\FlashTrans-1.8.0\FlashTrans.exe";
        const string old = @"C:\App\FlashTrans-1.7.6\FlashTrans.exe";

        if (!StartupService.Aligned($"\"{now}\" --tray", now))
            throw new InvalidOperationException("指着自己却说不一致");
        if (StartupService.Aligned($"\"{old}\" --tray", now))
            throw new InvalidOperationException("路径已经变了还说一致 —— 这条正是开机启不动的原因");
        if (StartupService.Aligned($"\"{now}\"", now))
            throw new InvalidOperationException("少了 --tray 也算一致，开机会弹出主窗口");
        if (StartupService.Aligned(null, now))
            throw new InvalidOperationException("压根没这条却说一致");

        // 同一个 exe 的不同写法不该被当成「变了」，否则每次启动都白写一次注册表
        if (!StartupService.Aligned("\"C:\\App\\sub\\..\\FlashTrans.exe\" --tray", @"C:\App\FlashTrans.exe"))
            throw new InvalidOperationException("同一个路径换个写法就认不出了");
        if (!StartupService.Aligned("\"c:\\app\\flashtrans.exe\" --tray", @"C:\App\FlashTrans.exe"))
            throw new InvalidOperationException("大小写不同就认不出了");
    }

    /// <summary>
    /// 自测程序的 bin 目录里也躺着一份 FlashTrans.exe。Sync 要是不认「谁在跑」，
    /// 跑一次自测就把用户的开机自启指到测试输出目录去了。
    /// </summary>
    static void RefusesFromForeignProcess()
    {
        var before = StartupService.CurrentCommand();
        if (StartupService.Sync(true))
            throw new InvalidOperationException("自测进程居然改动了 Run 项");
        if (StartupService.CurrentCommand() != before)
            throw new InvalidOperationException("Run 项被自测改了：" + StartupService.CurrentCommand());
    }
}
