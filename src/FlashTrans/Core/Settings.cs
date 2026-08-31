using System.Text.Json.Serialization;

namespace FlashTrans.Core;

[JsonConverter(typeof(JsonStringEnumConverter<SelectionMode>))]
public enum SelectionMode { Off, Icon, Direct }

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme { Dark, Light }

[JsonConverter(typeof(JsonStringEnumConverter<PopupPlace>))]
public enum PopupPlace { NearMouse, ScreenCenter, RememberLast }

/// <summary>截图选好之后对这块图做什么。</summary>
public enum CaptureAction
{
    /// <summary>什么都不做（取消）。</summary>
    None,
    Copy,
    Save,
    /// <summary>识别文字，送进主窗口。</summary>
    Ocr,
    /// <summary>识别文字并翻译。</summary>
    OcrTranslate,
}

public sealed class AppSettings
{
    /// <summary>配置结构版本，用于升级时补默认值，见 SettingsService.Migrate。</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>加新的默认源/字段时 +1，并在 Migrate 里补一段。</summary>
    public const int CurrentVersion = 3;

    // ------- 翻译源 -------
    public List<ProviderConfig> Providers { get; set; } = [];
    /// <summary>主用源 Id（标签页默认选中）。空则用第一个可用源。</summary>
    public string PrimaryProviderId { get; set; } = "";
    /// <summary>失败自动切换到下一个可用源。</summary>
    public bool AutoFallback { get; set; } = true;
    /// <summary>「聚合」标签页同时请求所有已启用源。</summary>
    public bool AggregateTab { get; set; } = true;
    public int MaxParallel { get; set; } = 6;

    // ------- 语言 -------
    public string SourceLang { get; set; } = Languages.Auto;
    public string TargetLang { get; set; } = "zh-CN";
    /// <summary>源文是中文时改译成该语言（中↔外自动互译）。</summary>
    public string SecondaryTarget { get; set; } = "en";
    public bool AutoSwapSameLang { get; set; } = true;
    /// <summary>同时翻译成多个语言。</summary>
    public bool MultiTargetEnabled { get; set; }
    public List<string> MultiTargets { get; set; } = ["zh-CN", "en", "ja"];
    /// <summary>常用语言，显示在语言菜单顶部。</summary>
    public List<string> FavoriteLangs { get; set; } = ["zh-CN", "en", "ja", "ko", "fr", "de", "ru", "es"];

    // ------- 显示 -------
    public bool Bilingual { get; set; }
    /// <summary>逐段对齐翻译（双语对照更整齐，请求略慢）。</summary>
    public bool BilingualByParagraph { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string AccentColor { get; set; } = "#4C8DFF";
    public double FontSize { get; set; } = 14;
    public string FontFamily { get; set; } = "";
    public bool Compact { get; set; } = true;
    public double Opacity { get; set; } = 1.0;
    public bool ShowLatency { get; set; } = true;

    // ------- 主窗口 -------
    public double WinLeft { get; set; } = double.NaN;
    public double WinTop { get; set; } = double.NaN;
    public double WinWidth { get; set; } = 560;
    public double WinHeight { get; set; } = 460;
    public bool AlwaysOnTop { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool RunAtStartup { get; set; }
    public bool TranslateOnType { get; set; } = true;
    // 550ms 太肉：打完最后一个字还要干等半秒多才开始请求。300ms 够滤掉连续击键，
    // 又不会让人觉得卡。真正的耗时在网络那边，防抖不该再叠一大截。
    public int TypeDelayMs { get; set; } = 300;
    public bool EnterToTranslate { get; set; } = true;

    // ------- 划词 / 弹窗 -------
    public SelectionMode SelectionMode { get; set; } = SelectionMode.Icon;
    /// <summary>划词需按住的修饰键：none/ctrl/alt/shift。</summary>
    public string SelectionModifier { get; set; } = "none";
    public bool RestoreClipboard { get; set; } = true;
    public bool MonitorClipboard { get; set; }
    public bool SkipOwnWindow { get; set; } = true;
    public int MaxSelectionChars { get; set; } = 5000;
    public PopupPlace PopupPlace { get; set; } = PopupPlace.NearMouse;
    public double PopupWidth { get; set; } = 420;
    public double PopupMaxHeight { get; set; } = 420;
    public bool PopupCloseOnBlur { get; set; } = true;
    /// <summary>「记住上次位置」用；手动调整过大小后也会记下来。</summary>
    public double PopupLeft { get; set; } = double.NaN;
    public double PopupTop { get; set; } = double.NaN;
    public double PopupHeight { get; set; } = double.NaN;
    /// <summary>弹窗里也显示源标签页（关掉更紧凑）。</summary>
    public bool PopupShowTabs { get; set; } = true;
    public bool DoubleCtrlWake { get; set; }

    // ------- 快捷键 -------
    public string HkTranslateSelection { get; set; } = "Ctrl+Alt+Q";
    public string HkToggleWindow { get; set; } = "Ctrl+Alt+W";
    public string HkTranslateClipboard { get; set; } = "Ctrl+Alt+E";
    public string HkToggleSelection { get; set; } = "Ctrl+Alt+S";
    public string HkNextProvider { get; set; } = "";
    public string HkCaptureOcr { get; set; } = "Ctrl+Alt+A";

    // ------- 截图 -------
    /// <summary>选好区域按回车（或双击选区）做什么。工具条上每个动作另有自己的快捷键。</summary>
    public CaptureAction CaptureEnterAction { get; set; } = CaptureAction.Copy;
    /// <summary>图片存哪儿。空 = 「图片」文件夹下的 FlashTrans。</summary>
    public string CaptureSaveDir { get; set; } = "";
    /// <summary>点「保存」时弹另存为对话框，而不是直接存进上面那个目录。</summary>
    public bool CaptureSaveAsk { get; set; }
    /// <summary>马赛克格子多大（图的像素）。</summary>
    public int CaptureMosaicBlock { get; set; } = 12;
    /// <summary>画笔粗细，也用于矩形、圆、箭头。</summary>
    public double CapturePenWidth { get; set; } = 3;
    /// <summary>画笔颜色，#RRGGBB。</summary>
    public string CapturePenColor { get; set; } = "#FF3B30";

    // ------- 截图里的文字标注 -------
    // 字号自己一个设置，不再从画笔粗细算：一条细箭头配一行大字是常事，
    // 绑在一起的话想要大字就得先把线也调粗。
    /// <summary>文字标注的字号（DIP，导出时按图的比例放大）。</summary>
    public double CaptureFontSize { get; set; } = 20;
    /// <summary>文字标注加粗。</summary>
    public bool CaptureFontBold { get; set; }
    /// <summary>文字标注用斜体。</summary>
    public bool CaptureFontItalic { get; set; }

    // ------- 截图工具条上的键 -------
    // 这些是蒙层里的键，不是全局热键，所以允许不带修饰键（蒙层期间没别人抢键盘）。
    // 留空 = 这个功能没有键，只能点工具条。
    public string CkRect { get; set; } = "R";
    public string CkEllipse { get; set; } = "O";
    public string CkArrow { get; set; } = "A";
    public string CkPen { get; set; } = "P";
    public string CkMosaic { get; set; } = "M";
    public string CkText { get; set; } = "T";
    public string CkUndo { get; set; } = "Ctrl+Z";
    /// <summary>重做。撤销多按了一下时把那一笔接回来。</summary>
    public string CkRedo { get; set; } = "Ctrl+Y";
    public string CkCopy { get; set; } = "Ctrl+C";
    public string CkSave { get; set; } = "Ctrl+S";
    public string CkOcr { get; set; } = "Ctrl+D";
    public string CkOcrTranslate { get; set; } = "Ctrl+Shift+D";
    public string CkLongShot { get; set; } = "Ctrl+L";

    // ------- 截图 OCR -------
    /// <summary>识别语言。空 = 跟着「源语言」，源语言是自动时用系统装的第一个 OCR 包。</summary>
    public string OcrLang { get; set; } = "";
    /// <summary>识别出的原文同时复制到剪贴板。</summary>
    public bool OcrCopyText { get; set; }

    // ------- 词典 -------
    public bool EudicEnabled { get; set; } = true;
    public string EudicPath { get; set; } = "";
    /// <summary>单词自动展示音标与释义。</summary>
    public bool ShowDictionary { get; set; } = true;

    // ------- 其它 -------
    public bool CacheEnabled { get; set; } = true;
    public int CacheSize { get; set; } = 2000;
    /// <summary>缓存条目存活小时数，过期后自动清理。</summary>
    public int CacheTtlHours { get; set; } = 12;
    public string Proxy { get; set; } = "";
    public bool WarmupOnStart { get; set; } = true;

    public ProviderConfig? Find(string id) => Providers.FirstOrDefault(p => p.Id == id);

    public IEnumerable<ProviderConfig> EnabledProviders => Providers.Where(p => p.Enabled);

    public List<string> ResolveTargets()
    {
        if (MultiTargetEnabled && MultiTargets.Count > 0)
            return MultiTargets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return [TargetLang];
    }

    public static AppSettings CreateDefault()
    {
        var s = new AppSettings();
        s.Providers.Add(ProviderConfig.Create(ProviderKind.GoogleFree));
        s.Providers.Add(ProviderConfig.Create(ProviderKind.BingFree));
        s.Providers.Add(ProviderConfig.Create(ProviderKind.YoudaoFree));
        // 免费、不要 Key、国内直连，默认开着；谷歌在墙内连不上时它顶得住。
        s.Providers.Add(ProviderConfig.Create(ProviderKind.TranSmart));
        var deepl = ProviderConfig.Create(ProviderKind.DeepL);
        deepl.Enabled = false;
        s.Providers.Add(deepl);
        var ai = ProviderConfig.Create(ProviderKind.OpenAiCompat, "AI 翻译");
        ai.Enabled = false;
        s.Providers.Add(ai);
        s.PrimaryProviderId = s.Providers[0].Id;
        return s;
    }
}
