using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using SelectionMode = FlashTrans.Core.SelectionMode;

namespace FlashTrans.Views;

public sealed partial class SettingsWindow
{
    // ============================================================= 通用

    UIElement BuildGeneralPage()
    {
        var page = Page();

        Section(page, "启动",
            Check("开机自动启动（静默驻留托盘）", StartupService.IsEnabled(), on =>
            {
                if (!StartupService.Set(on)) Toast("写入启动项失败，可能被安全软件拦截");
                S.RunAtStartup = on;
            }),
            Check("启动时最小化到托盘", S.StartMinimized, on => S.StartMinimized = on,
                "关掉则启动时直接显示主窗口"),
            Check("关闭窗口时回到托盘", S.CloseToTray, on => S.CloseToTray = on),
            Check("启动时预热网络连接", S.WarmupOnStart, on => S.WarmupOnStart = on,
                "提前建立 TLS 连接，第一次翻译更快"));

        Section(page, "输入与响应",
            Check("边输入边翻译", S.TranslateOnType, on => S.TranslateOnType = on),
            Field("输入停顿延迟", Number(S.TypeDelayMs, 120, 3000, v => S.TypeDelayMs = v, "毫秒"),
                "停止输入多久后自动翻译"),
            Check("回车翻译，Shift+回车换行", S.EnterToTranslate, on => S.EnterToTranslate = on,
                "关掉则回车换行，Ctrl+回车翻译"),
            Field("失败自动切换", Check("出错时自动尝试下一个可用源", S.AutoFallback,
                on => S.AutoFallback = on)),
            Field("并发上限", Number(S.MaxParallel, 1, 16, v => S.MaxParallel = v, "个请求"),
                "聚合标签同时请求的源数量"));

        Section(page, "划词翻译",
            Field("划词开关", Combo<SelectionMode>(
                [("关闭划词", SelectionMode.Off), ("选中后显示小图标（推荐）", SelectionMode.Icon),
                 ("选中后直接弹出译文", SelectionMode.Direct)],
                S.SelectionMode, v => S.SelectionMode = v, width: 230),
                $"选「关闭划词」即不再弹小图标；托盘右键或 {HotkeySpec.Parse(S.HkToggleSelection)} 也能随时开关"),
            Field("需按住的键", Combo<string>(
                [("不需要", "none"), ("Ctrl", "ctrl"), ("Alt", "alt"), ("Shift", "shift")],
                S.SelectionModifier.ToLowerInvariant(), v => S.SelectionModifier = v),
                "按住指定键划词才触发，可避免误弹"),
            Check("双击 Ctrl 翻译选中文本", S.DoubleCtrlWake, on => S.DoubleCtrlWake = on),
            Check("翻译后还原剪贴板", S.RestoreClipboard, on => S.RestoreClipboard = on,
                "取词要借用剪贴板，勾选后会恢复原内容"),
            Check("忽略本程序窗口内的选择", S.SkipOwnWindow, on => S.SkipOwnWindow = on),
            Check("监听剪贴板，复制即翻译", S.MonitorClipboard, on => S.MonitorClipboard = on),
            Field("最长取词字数", Number(S.MaxSelectionChars, 100, 20000, v => S.MaxSelectionChars = v, "字")));

        Section(page, "弹窗",
            Field("出现位置", Combo<PopupPlace>(
                [("鼠标附近", PopupPlace.NearMouse), ("屏幕中央", PopupPlace.ScreenCenter),
                 ("上次的位置", PopupPlace.RememberLast)],
                S.PopupPlace, v => S.PopupPlace = v)),
            Field("宽度", SliderRow(S.PopupWidth, 280, 720, 10, v => S.PopupWidth = v, v => $"{v:F0} px")),
            Field("最大高度", SliderRow(S.PopupMaxHeight, 200, 1600, 20, v => S.PopupMaxHeight = v,
                v => $"{v:F0} px"), "调再高也不会超出屏幕工作区"),
            Check("弹窗内显示源标签", S.PopupShowTabs, on => S.PopupShowTabs = on),
            Check("弹窗一直置顶", S.PopupTopmost, on => S.PopupTopmost = on,
                "关掉时弹出仍会置顶一次保证看得见，但焦点一走就让位给你点的那个窗口"));

        Section(page, "缓存与网络",
            Check("启用翻译缓存", S.CacheEnabled, on => S.CacheEnabled = on,
                "相同文本在保留时长内直接返回，几乎零延迟"),
            Field("缓存条数", Number(S.CacheSize, 100, 20000, v => S.CacheSize = v, "条")),
            Field("保留时长", Number(S.CacheTtlHours, 1, 168, v => S.CacheTtlHours = v, "小时"),
                "过期条目会自动清理，不必手动清空"),
            Field("代理", Input(S.Proxy, v => S.Proxy = v, "http://127.0.0.1:7890"),
                "留空表示使用系统代理"),
            UiKit.Row(8,
                SmallButton("清空缓存", () =>
                {
                    var n = Engine.Cache.Count;
                    Engine.Cache.Clear();
                    Toast(n > 0 ? $"已清空 {n} 条缓存" : "缓存本来就是空的");
                }),
                SmallButton("重置所有冷却", () => { Engine.Health.Reset(); Toast("已重置"); }),
                SmallButton("应用代理设置", () => { Net.Configure(S.Proxy); Toast("代理已应用"); })));

        return page;
    }

    // ------------------------------------------------------------- 截图

    /// <summary>截图页。自己一栏：动作默认值、画笔、蒙层里的那些键、识别语言。</summary>
    UIElement BuildCapturePage()
    {
        var page = Page();

        var hk = HotkeySpec.Parse(S.HkCaptureOcr).ToString();
        var press = string.IsNullOrWhiteSpace(hk) ? "在托盘右键菜单里选「截图」" : $"按 {hk}";

        Section(page, "截图",
            Hint($"{press} 后框选一块区域，选区下面会出来工具条：矩形、圆、箭头、画笔、马赛克、"
                 + "文字，还有复制、保存、识别文字、识别并翻译、长截图、录制动图。"),
            Field("按回车时", Combo(new (string, CaptureAction)[]
            {
                ("复制到剪贴板", CaptureAction.Copy),
                ("保存成图片", CaptureAction.Save),
                ("识别文字", CaptureAction.Ocr),
                ("识别并翻译", CaptureAction.OcrTranslate),
            }, S.CaptureEnterAction, v => S.CaptureEnterAction = v, width: 230),
                "双击选区也走这个动作"),
            Field("保存到", SaveDirRow(), "留空放在图片文件夹"),
            Check("点保存时先问存到哪儿", S.CaptureSaveAsk, on => S.CaptureSaveAsk = on,
                "关掉就直接存进上面那个目录，不打断"),
            LeftRow(SmallButton("试一下", () => _ = TryCaptureAsync())));

        Section(page, "录制动图",
            Hint("框选之后点工具条上的「录制」，那块区域开始录，`Esc` 或者点浮条停下。"
                 + "录的是实时画面，不是刚才那张定格图——画的标注不会进动图。"
                 + "存进上面那个保存目录。"),
            Field("格式", Combo(new (string, RecordFormat)[]
            {
                ("WebP（体积小很多）", RecordFormat.Webp),
                ("GIF（谁都打得开）", RecordFormat.Gif),
            }, S.RecordFormat, v => S.RecordFormat = v, width: 230),
                Img2WebpNote()),
            Field("帧率", SliderRow(S.RecordFps,
                    RecordService.MinFps, RecordService.MaxFps, 1,
                    v => S.RecordFps = (int)Math.Round(v), v => $"{v:F0} fps"),
                "调太高界面自己会卡，反而录不满这个帧率；体积基本按帧数线性涨"),
            Field("最长", SliderRow(S.RecordMaxSeconds,
                    RecordService.MinSeconds, RecordService.MaxSeconds, 1,
                    v => S.RecordMaxSeconds = (int)Math.Round(v), v => $"{v:F0} 秒"),
                "到点自己停，给「按了开始就走开」兜底"));

        Section(page, "画笔",
            Hint("这儿设的是每次截图的起始值。真正画的时候在工具条上随时能改："
                 + "选了工具，工具条第二行就是它的参数——粗细、字号、马赛克格子，"
                 + "有预设也能一格一格加减，`Ctrl+滚轮` 也是调这个。"
                 + "改完会套到刚画完的那一笔上，所以画完再换也行，不用撤销重画。"),
            Hint("画的时候按住 `Shift`：矩形和马赛克出正方形、圆出正圆、箭头吸到 15° 的整数倍，"
                 + "画笔变成拉直线（工具条上没有单独的直线工具，画下划线就用它）。"
                 + "画完退出工具，点中哪一笔它身上就出来几个圆点，拖着能改形状——"
                 + "方框大了一点不用删掉重画。"),
            Field("画笔粗细", SliderRow(S.CapturePenWidth,
                    CaptureLimits.MinPenWidth, CaptureLimits.MaxPenWidth, 1,
                    v => S.CapturePenWidth = v, v => $"{v:F0} px"),
                "矩形、圆、箭头也用这个粗细"),
            Field("马赛克格子", Number(S.CaptureMosaicBlock,
                    CaptureLimits.MinMosaicBlock, CaptureLimits.MaxMosaicBlock,
                    v => S.CaptureMosaicBlock = v, "px"),
                "格子越大遮得越死"));

        Section(page, "文字标注",
            Hint("字号跟画笔粗细是分开的两个值——细箭头配一行大字是常事，"
                 + "绑在一起的话想要大字就得先把线也调粗。工具条上 `Ctrl+B` 加粗、"
                 + "`Ctrl+I` 斜体，正在打字时也管用。"),
            Field("字号", SliderRow(S.CaptureFontSize,
                    CaptureLimits.MinFontSize, CaptureLimits.MaxFontSize, 1,
                    v => S.CaptureFontSize = v, v => $"{v:F0} px"),
                "字带描边，什么底色都看得清"),
            Check("默认加粗", S.CaptureFontBold, on => S.CaptureFontBold = on,
                "花底色上加粗比不加粗好认不少"),
            Check("默认斜体", S.CaptureFontItalic, on => S.CaptureFontItalic = on));

        Section(page, "截图工具的键",
            Hint("这些是框选期间才管用的键，不是全局热键，所以可以不带 Ctrl / Alt。"
                 + "点输入框后直接按；Backspace 清掉表示这个功能只能点工具条。"
                 + "Esc（退出）、回车（确认）、空格（选中鼠标下的窗口）是固定的，不能改。"
                 + "重做除了下面配的键，`Ctrl+Shift+Z` 也一直管用。"),
            CaptureKeyField("矩形", S.CkRect, v => S.CkRect = v),
            CaptureKeyField("圆", S.CkEllipse, v => S.CkEllipse = v),
            CaptureKeyField("箭头", S.CkArrow, v => S.CkArrow = v),
            CaptureKeyField("画笔", S.CkPen, v => S.CkPen = v),
            CaptureKeyField("马赛克", S.CkMosaic, v => S.CkMosaic = v),
            CaptureKeyField("文字", S.CkText, v => S.CkText = v),
            CaptureKeyField("撤销", S.CkUndo, v => S.CkUndo = v),
            CaptureKeyField("重做", S.CkRedo, v => S.CkRedo = v),
            CaptureKeyField("复制", S.CkCopy, v => S.CkCopy = v),
            CaptureKeyField("保存", S.CkSave, v => S.CkSave = v),
            CaptureKeyField("识别文字", S.CkOcr, v => S.CkOcr = v),
            CaptureKeyField("识别并翻译", S.CkOcrTranslate, v => S.CkOcrTranslate = v),
            CaptureKeyField("长截图", S.CkLongShot, v => S.CkLongShot = v),
            CaptureKeyField("录制动图", S.CkRecord, v => S.CkRecord = v));

        Section(page, "文字识别", BuildOcrRows());

        return page;
    }

    UIElement[] BuildOcrRows()
    {
        if (!OcrService.IsAvailable)
            return
            [
                Hint("识别文字要用系统的语言包，现在没装，工具条上那两个识别按钮会提示装。"
                     + OcrService.NoEngineHint()),
            ];

        var langs = new List<(string, string)> { ("跟随源语言（推荐）", "") };
        langs.AddRange(OcrService.AvailableLanguages.Select(t => (OcrService.DisplayName(t), t)));

        return
        [
            Hint("点「识别文字」之后会弹一个框，认出来的字摆在里面，可以改完再复制或翻译。"),
            Field("识别语言", Combo(langs, S.OcrLang, v => S.OcrLang = v, width: 230),
                "截图里是什么语言就选什么，选错会多认错字"),
            Check("「识别并翻译」时把原文也复制到剪贴板", S.OcrCopyText, on => S.OcrCopyText = on,
                "图片和文字一起进剪贴板，粘到哪儿由对方挑"),
        ];
    }

    /// <summary>
    /// 格式那一栏底下的说明。WebP 要靠外部的 img2webp.exe，
    /// 装没装直接影响会存成什么，所以这里如实说现在是哪种情况。
    /// </summary>
    static string Img2WebpNote()
        => AnimEncoder.WebpAvailable
            ? "WebP 可用。同样画面通常比 GIF 小一个数量级，而且不会被砍成 256 色"
            : $"没找到 {AnimEncoder.Img2WebpRelative}，选 WebP 也会存成 GIF。"
              + "把它放到那个位置就能出真 WebP";

    /// <summary>
    /// 保存目录：输入框加两个按钮。
    /// 用 Grid 而不是 Row：输入框给固定宽度的话，卡片一窄按钮就被挤出去看不见了。
    /// </summary>
    UIElement SaveDirRow()
    {
        var box = Input(S.CaptureSaveDir, v => S.CaptureSaveDir = v.Trim());
        box.ToolTip = "留空 = " + AppHost.CaptureDir();

        var pick = SmallButton("选目录", () =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "截图保存到哪个文件夹",
                    InitialDirectory = AppHost.CaptureDir(),
                };
                if (dlg.ShowDialog(this) != true) return;
                // 写 Text 会走 TextChanged，设置值那步 Input 已经代劳；
                // 但它只在失焦时存盘，这里是程序改的，得自己存一次
                box.Text = dlg.FolderName;
                Save();
            });

        var open = SmallButton("打开", () =>
            {
                var dir = AppHost.CaptureDir();
                try
                {
                    System.IO.Directory.CreateDirectory(dir);
                    OpenUrl(dir);
                }
                catch (Exception ex) { Toast("打不开目录：" + ex.Message); }
            });
        open.ToolTip = "在资源管理器里打开保存目录";

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pick.Margin = new Thickness(6, 0, 0, 0);
        open.Margin = new Thickness(6, 0, 0, 0);
        UiKit.SetGrid(box, col: 0);
        UiKit.SetGrid(pick, col: 1);
        UiKit.SetGrid(open, col: 2);
        grid.Children.Add(box);
        grid.Children.Add(pick);
        grid.Children.Add(open);
        return grid;
    }

    /// <summary>「试一下」：把设置窗口藏起来走一遍截图识别，回来再显示。</summary>
    async Task TryCaptureAsync()
    {
        // 本窗口是置顶的，留着会被拍进截图里，也会挡住用户想截的东西
        var closed = false;
        void Mark(object? s, EventArgs e) => closed = true;
        Closed += Mark;
        Hide();
        try { await _host.CaptureAsync(); }
        finally
        {
            Closed -= Mark;
            if (!closed) { Show(); Activate(); }   // 中途被关掉就别再弹回来
        }
    }

    /// <summary>卡片里的行是拉满宽的，按钮得自己靠左，不然会拉成一整条。</summary>
    static UIElement LeftRow(UIElement child)
    {
        var row = UiKit.Row(8, child);
        row.HorizontalAlignment = HorizontalAlignment.Left;
        return row;
    }

    // ============================================================= 语言

    UIElement BuildLanguagesPage()
    {
        var page = Page();

        var fromPicker = new LangPicker(includeAuto: true) { SelectedCode = S.SourceLang };
        fromPicker.HorizontalAlignment = HorizontalAlignment.Left;
        fromPicker.SelectionChanged += code => { S.SourceLang = code; Save(); };

        var toPicker = new LangPicker { SelectedCode = S.TargetLang };
        toPicker.HorizontalAlignment = HorizontalAlignment.Left;
        toPicker.SelectionChanged += code => { S.TargetLang = code; Save(); };

        var secondPicker = new LangPicker { SelectedCode = S.SecondaryTarget };
        secondPicker.HorizontalAlignment = HorizontalAlignment.Left;
        secondPicker.SelectionChanged += code => { S.SecondaryTarget = code; Save(); };

        Section(page, "默认语言",
            Field("源语言", fromPicker),
            Field("目标语言", toPicker, "默认中文，可随时改"),
            Check("源文已是目标语言时自动互译", S.AutoSwapSameLang, on => S.AutoSwapSameLang = on),
            Field("互译时改译成", secondPicker, "例如目标是中文、原文也是中文，就译成这个语言"));

        Section(page, "多语言同时翻译",
            Check("启用", S.MultiTargetEnabled, on => S.MultiTargetEnabled = on,
                "一次请求翻译成下面勾选的所有语言"),
            BuildMultiTargetEditor());

        Section(page, "常用语言",
            Hint("显示在语言菜单顶部与弹窗的快速切换里。"),
            BuildFavoriteEditor());

        return page;
    }

    UIElement BuildMultiTargetEditor()
    {
        var panel = new StackPanel();
        var chips = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };

        void Refresh()
        {
            chips.Children.Clear();
            foreach (var code in S.MultiTargets.ToList())
                chips.Children.Add(RemovableChip(Languages.NameOf(code), () =>
                {
                    if (S.MultiTargets.Count <= 1) { Toast("至少保留一个目标语言"); return; }
                    S.MultiTargets.Remove(code);
                    Save();
                    Refresh();
                }));
        }
        Refresh();

        var picker = new LangPicker { SelectedCode = "en" };
        var add = SmallButton("添加", () =>
        {
            var code = picker.SelectedCode;
            if (S.MultiTargets.Contains(code, StringComparer.OrdinalIgnoreCase)) return;
            S.MultiTargets.Add(code);
            Save();
            Refresh();
        });

        panel.Children.Add(chips);
        var row = UiKit.Row(8, picker, add);
        row.Margin = new Thickness(0, 8, 0, 0);
        row.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(row);
        return panel;
    }

    UIElement BuildFavoriteEditor()
    {
        var panel = new StackPanel();
        var chips = new WrapPanel();

        void Refresh()
        {
            chips.Children.Clear();
            foreach (var code in S.FavoriteLangs.ToList())
                chips.Children.Add(RemovableChip(Languages.NameOf(code), () =>
                {
                    S.FavoriteLangs.Remove(code);
                    Save();
                    Refresh();
                }));
        }
        Refresh();

        var picker = new LangPicker { SelectedCode = "de" };
        var add = SmallButton("添加", () =>
        {
            var code = picker.SelectedCode;
            if (S.FavoriteLangs.Contains(code, StringComparer.OrdinalIgnoreCase)) return;
            S.FavoriteLangs.Add(code);
            Save();
            Refresh();
        });

        panel.Children.Add(chips);
        var row = UiKit.Row(8, picker, add);
        row.Margin = new Thickness(0, 8, 0, 0);
        row.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(row);
        return panel;
    }

    static UIElement RemovableChip(string label, Action onRemove)
    {
        var text = UiKit.Text(label, 11.5, "Text");
        var close = UiKit.IconButton(UiKit.IconClose, "移除", (_, _) => onRemove(), 8);
        close.Width = 16;
        close.Height = 16;
        close.Margin = new Thickness(5, 0, 0, 0);

        var content = UiKit.Row(0, text, close);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 3, 5, 3),
            Margin = new Thickness(0, 0, 6, 6),
            Child = content,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "BgHover");
        chip.SetResourceReference(Border.BorderBrushProperty, "Border");
        return chip;
    }

    // ============================================================= 快捷键

    UIElement BuildHotkeyPage()
    {
        var page = Page();

        Section(page, "全局快捷键",
            Hint("点输入框后直接按组合键；按 Backspace 清除。修饰键至少要有一个 Ctrl / Alt / Shift / Win。"),
            HotkeyField("翻译选中文本", S.HkTranslateSelection, v => S.HkTranslateSelection = v),
            HotkeyField("显示 / 隐藏主窗口", S.HkToggleWindow, v => S.HkToggleWindow = v),
            HotkeyField("翻译剪贴板内容", S.HkTranslateClipboard, v => S.HkTranslateClipboard = v),
            HotkeyField("开关划词翻译", S.HkToggleSelection, v => S.HkToggleSelection = v),
            HotkeyField("切换到下一个源", S.HkNextProvider, v => S.HkNextProvider = v),
            HotkeyField("截图", S.HkCaptureOcr, v => S.HkCaptureOcr = v),
            HotkeyField("收起 / 叫回翻译弹窗", S.HkTogglePopup, v => S.HkTogglePopup = v),
            Hint("弹窗收起后内容还留着，再按一次原样回来；期间来了新翻译，收起的那个就作废了。"));

        Section(page, "框选时的键",
            Hint("矩形、画笔、马赛克那些键在「截图」一栏里配，它们只在框选期间管用。"),
            KeyInfo("Esc", "退工具 / 取消框选"),
            KeyInfo("Enter", "按「截图」栏里配的那个动作收尾"),
            KeyInfo("Space", "选中鼠标底下那个窗口"),
            KeyInfo("Ctrl + A", "选中整个桌面"),
            KeyInfo("Ctrl + 滚轮", "调画笔粗细"));

        Section(page, "窗口内快捷键",
            KeyInfo("Ctrl + Enter / Enter", "翻译"),
            KeyInfo("Ctrl + Tab", "下一个翻译源"),
            KeyInfo("Ctrl + 1 … 9", "切到第 N 个源"),
            KeyInfo("Ctrl + Shift + S", "交换源语言与目标语言"),
            KeyInfo("Ctrl + D", "双语对照开关（弹窗里是查词典）"),
            KeyInfo("Ctrl + L", "清空输入"),
            KeyInfo("Ctrl + ,", "打开设置"),
            KeyInfo("Esc", "清空输入 / 关闭窗口"));

        return page;
    }

    UIElement HotkeyField(string label, string current, Action<string> onChange)
    {
        var box = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(current) ? "" : HotkeySpec.Parse(current).ToString(),
            FontSize = 12.5,
            Height = 28,
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsReadOnly = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(7, 0, 7, 0),
            ToolTip = "点这里然后按组合键",
        };

        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key is Key.Back or Key.Delete)
            {
                box.Text = "";
                onChange("");
                Save();
                return;
            }
            if (key == Key.Escape) return;

            var spec = HotkeySpec.FromKeyEvent(key, Keyboard.Modifiers);
            if (spec is null)
            {
                if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                    or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin))
                    Toast("请至少加一个 Ctrl / Alt / Shift / Win");
                return;
            }
            box.Text = spec.ToString();
            onChange(spec.ToString());
            Save();
        };
        box.GotFocus += (_, _) => box.SelectAll();

        return Field(label, box);
    }

    /// <summary>
    /// 蒙层里那些键的录制框。跟 HotkeyField 的区别只有一条：不要求带修饰键。
    /// 全局热键必须带修饰键，否则会抢掉别的程序里的普通打字；
    /// 蒙层期间整个屏幕都是自己的，按个 R 就切矩形正是要的效果。
    /// </summary>
    UIElement CaptureKeyField(string label, string current, Action<string> onChange)
    {
        var box = new TextBox
        {
            Text = HotkeySpec.Parse(current).ToString(),
            FontSize = 12.5,
            Height = 28,
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsReadOnly = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(7, 0, 7, 0),
            ToolTip = "点这里然后按键，可以不带 Ctrl / Alt",
        };

        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key is Key.Back or Key.Delete)
            {
                box.Text = "";
                onChange("");
                Save();
                return;
            }
            // 这四个在蒙层里另有固定用途，让人设过去只会把自己锁在里面
            if (key is Key.Escape or Key.Enter or Key.Space or Key.Tab)
            {
                Toast("Esc / 回车 / 空格 / Tab 在框选时另有用途，不能改");
                return;
            }
            // 光按住修饰键还不算一个键，等真正的那个键按下来
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
                or Key.None or Key.ImeProcessed) return;

            var spec = new HotkeySpec(Keyboard.Modifiers, key);
            box.Text = spec.ToString();
            onChange(spec.ToString());
            Save();
        };
        box.GotFocus += (_, _) => box.SelectAll();

        return Field(label, box);
    }

    static UIElement KeyInfo(string keys, string what)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var k = UiKit.Text(keys, 11.5, "TextDim");
        UiKit.SetGrid(k, col: 0);
        grid.Children.Add(k);

        var v = UiKit.Text(what, 11.5, "Text");
        UiKit.SetGrid(v, col: 1);
        grid.Children.Add(v);

        grid.Margin = new Thickness(0, 3, 0, 3);
        return grid;
    }

    // ============================================================= 外观与词典

    UIElement BuildAppearancePage()
    {
        var page = Page();

        Section(page, "主题",
            Field("配色", Combo<AppTheme>([("深色", AppTheme.Dark), ("浅色", AppTheme.Light)],
                S.Theme, v => { S.Theme = v; ThemeService.ApplyTheme(v); ThemeService.ApplyAccent(S.AccentColor); })),
            Field("强调色", BuildAccentPicker()),
            Field("字号", SliderRow(S.FontSize, 11, 22, 0.5, v => S.FontSize = v, v => $"{v:F1}")),
            Field("字体", Input(S.FontFamily, v => S.FontFamily = v, "留空用系统默认"),
                "例如 微软雅黑 / Segoe UI"),
            Field("窗口不透明度", SliderRow(S.Opacity, 0.5, 1.0, 0.05, v => S.Opacity = v,
                v => $"{v * 100:F0}%")),
            Check("紧凑模式", S.Compact, on => S.Compact = on, "更小的内边距，一屏放更多内容"),
            Check("显示耗时", S.ShowLatency, on => S.ShowLatency = on));

        Section(page, "结果显示",
            Check("双语对照", S.Bilingual, on => S.Bilingual = on, "原文与译文成对显示"),
            Check("逐段对齐", S.BilingualByParagraph, on => S.BilingualByParagraph = on,
                "多行文本逐段翻译后再配对，排版更整齐，请求略多"),
            Check("显示聚合标签", S.AggregateTab, on => S.AggregateTab = on,
                "一个标签里同时看所有源的结果"),
            Check("单词显示音标与释义", S.ShowDictionary, on => S.ShowDictionary = on));

        Section(page, "欧路词典",
            Check("启用欧路词典查询", S.EudicEnabled, on => S.EudicEnabled = on,
                "结果区的「词典」按钮会把单词发给欧路词典"),
            Field("程序路径", BuildEudicPathRow(), "留空自动探测（先试 eudic:// 协议）"),
            UiKit.Row(8,
                SmallButton("自动探测", () =>
                {
                    var p = EudicService.DetectPath();
                    if (p is null) Toast("没找到欧路词典，请手动指定 eudic.exe");
                    else { S.EudicPath = p; Save(); _pageHost.Content = BuildAppearancePage(); Toast("已找到：" + p); }
                }),
                SmallButton("测试查词", () => Toast(EudicService.Lookup("hello")
                    ? "已发送 hello 到欧路词典" : "调用失败，检查路径"))));

        return page;
    }

    UIElement BuildAccentPicker()
    {
        var wrap = new WrapPanel();
        var custom = Input(S.AccentColor, v => { }, "#4C8DFF", width: 96);

        foreach (var hex in ThemeService.AccentPresets)
        {
            var color = ThemeService.Parse(hex, System.Windows.Media.Colors.SteelBlue);
            var swatch = new Border
            {
                Width = 22, Height = 22,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 6, 0),
                Background = new System.Windows.Media.SolidColorBrush(color),
                BorderThickness = new Thickness(
                    string.Equals(hex, S.AccentColor, StringComparison.OrdinalIgnoreCase) ? 2 : 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = hex,
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "Text");
            var h = hex;
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                S.AccentColor = h;
                custom.Text = h;
                ThemeService.ApplyAccent(h);
                Save();
                foreach (var child in wrap.Children)
                    if (child is Border b && b.ToolTip is string t)
                        b.BorderThickness = new Thickness(
                            string.Equals(t, h, StringComparison.OrdinalIgnoreCase) ? 2 : 0);
            };
            wrap.Children.Add(swatch);
        }

        custom.LostFocus += (_, _) =>
        {
            var hex = custom.Text.Trim();
            if (hex.Length == 0) return;
            S.AccentColor = hex;
            ThemeService.ApplyAccent(hex);
            Save();
        };
        wrap.Children.Add(custom);
        return wrap;
    }

    UIElement BuildEudicPathRow()
    {
        var box = Input(S.EudicPath, v => S.EudicPath = v, "eudic.exe 完整路径");
        var browse = SmallButton("浏览…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择欧路词典程序",
                Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == true)
            {
                box.Text = dlg.FileName;
                S.EudicPath = dlg.FileName;
                Save();
            }
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        UiKit.SetGrid(box, col: 0);
        grid.Children.Add(box);
        browse.Margin = new Thickness(6, 0, 0, 0);
        UiKit.SetGrid(browse, col: 1);
        grid.Children.Add(browse);
        return grid;
    }

    // ============================================================= 关于

    UIElement BuildAboutPage()
    {
        var page = Page();
        var ver = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        Section(page, "闪译 FlashTrans",
            UiKit.Text($"版本 {ver} · WPF / .NET 9 · 不依赖第三方库", 12.5, "Text"),
            Hint("聚合免费与自带额度的翻译接口，标签页切换、失败自动降级、划词与快捷键唤出。"),
            Hint("配置文件：" + SettingsService.Instance.ConfigPath),
            Hint("API 密钥使用 Windows DPAPI 加密后保存，只有当前 Windows 账户能解开。"));

        Section(page, "维护",
            UiKit.Row(8,
                SmallButton("打开日志文件", () =>
                {
                    if (System.IO.File.Exists(Log.Path)) OpenUrl(Log.Path);
                    else Toast("还没有日志");
                }),
                SmallButton("重置为默认设置", ResetAll, "OutlineBtn")));

        return page;
    }

    void ResetAll()
    {
        if (!AppDialog.Confirm(this, "重置为默认设置",
                "所有设置都会回到初始状态，确定吗？",
                okText: "重置", tone: DialogTone.Danger,
                detail: "已配置的翻译源、API 密钥、快捷键、外观偏好全部清空，无法撤销。"))
            return;

        SettingsService.Instance.Apply(AppSettings.CreateDefault());
        _pageHost.Content = BuildAboutPage();
        Toast("已恢复默认设置");
    }
}
