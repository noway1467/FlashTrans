using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashTrans.Core;
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
                    var apply = Descendants<Button>(palette).Single(b => b.Name == "ApplyColor");
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
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
                    foreach (var tab in new[] { "general", "sources", "languages", "capture", "ocr", "quicklaunch", "hotkeys", "appearance", "about" })
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
            foreach (var hotkey in new[] { s.HkTranslateSelection, s.HkCaptureOcr, s.HkTranslateClipboard, s.HkQuickLaunch }.Where(h => h.Length > 0))
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
        step("开机自启：从 Run 项的命令里取出 exe 路径", ParseCommand);
        step("开机自启：程序换了目录要认出来路径已经失效", DetectsMovedExe);
        step("开机自启：自测程序不许去改用户的 Run 项", RefusesFromForeignProcess);
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

        var brokenOcrSize = AppSettings.CreateDefault();
        brokenOcrSize.OcrResultWidth = double.NaN;
        brokenOcrSize.OcrResultHeight = double.PositiveInfinity;
        SettingsService.Normalize(brokenOcrSize);
        if (brokenOcrSize.OcrResultWidth != 520 || brokenOcrSize.OcrResultHeight != 360)
            throw new InvalidOperationException("损坏的 OCR 结果窗口尺寸没有回退到默认值");

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
