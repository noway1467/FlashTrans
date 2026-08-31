using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

public sealed partial class CaptureOverlay
{
    const double BarHeight = 40;
    /// <summary>第二行（当前工具的参数）多高。只在有参数可调时才占这个高度。</summary>
    const double StyleRowHeight = 34;
    const double BarGap = 8;

    static readonly Brush BarBack = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x20, 0x26)));
    static readonly Brush BarEdge = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    static readonly Brush BarText = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEC)));
    /// <summary>第二行「粗细」「字号」那种小标签。比正文暗一点，但仍够 4.5:1。</summary>
    static readonly Brush BarDim = Frozen(new SolidColorBrush(Color.FromRgb(0xA8, 0xAE, 0xB8)));
    static readonly Brush OnBack = Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)));

    /// <summary>八个可选颜色，够用又不至于摆一排。</summary>
    static readonly string[] Palette =
        ["#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#4C8DFF", "#AF52DE", "#FFFFFF", "#1C1C1E"];

    // 工具图标自己画。用 ✎ ▩ 这类字符的话，系统会拿彩色 emoji 字体去凑，
    // 一排线条图标里混进个彩色铅笔很难看，而且换台机器字体不同就变样。
    static readonly Geometry IconRect = Geometry.Parse("M2.5,4 H13.5 V12 H2.5 Z");
    static readonly Geometry IconEllipse = Geometry.Parse(
        "M8,2.5 C11.3,2.5 13.5,5 13.5,8 C13.5,11 11.3,13.5 8,13.5 C4.7,13.5 2.5,11 2.5,8 C2.5,5 4.7,2.5 8,2.5 Z");
    static readonly Geometry IconArrow = Geometry.Parse("M3,13 L12.5,3.5 M8,3.5 H12.5 V8");
    static readonly Geometry IconPen = Geometry.Parse("M3,13 L4.2,9.8 L10.6,3.4 L12.6,5.4 L6.2,11.8 Z M9.4,4.6 L11.4,6.6");
    static readonly Geometry IconMosaic = Geometry.Parse(
        "M2.5,3 H6 V6.5 H2.5 Z M9,3 H12.5 V6.5 H9 Z M6,6.5 H9.5 V10 H6 Z M2.5,10 H6 V13.5 H2.5 Z M9,10 H12.5 V13.5 H9 Z");
    static readonly Geometry IconUndo = Geometry.Parse(
        "M3,7 H9.5 C11.4,7 13,8.4 13,10.2 C13,12 11.4,13.4 9.5,13.4 H6 M3,7 L6,4 M3,7 L6,10");
    // 重做就是把撤销那条路径左右翻过来（每个 x 换成 16-x），两个并排才像一对
    static readonly Geometry IconRedo = Geometry.Parse(
        "M13,7 H6.5 C4.6,7 3,8.4 3,10.2 C3,12 4.6,13.4 6.5,13.4 H10 M13,7 L10,4 M13,7 L10,10");
    static readonly Geometry IconLong = Geometry.Parse(
        "M8,2.5 V13.5 M5,5.5 L8,2.5 L11,5.5 M5,10.5 L8,13.5 L11,10.5");
    static readonly Geometry IconCross = Geometry.Parse("M4,4 L12,12 M12,4 L4,12");

    readonly List<(CaptureTool Tool, Button Btn)> _toolButtons = [];
    Button? _undoBtn;
    Button? _redoBtn;

    /// <summary>第二行现在摆的是哪种参数。</summary>
    enum StyleCtx { None, Width, Font, Mosaic }

    StyleCtx _styleCtx = StyleCtx.None;
    StackPanel? _styleRow;
    Border? _styleRowHost;
    /// <summary>第二行上要跟着值刷新的那几个东西，重建一行时一起换掉。</summary>
    TextBlock? _styleValue;
    List<(double Value, Border Chip)> _styleChips = [];
    Button? _boldBtn;
    Button? _italicBtn;

    /// <summary>
    /// 工具条上按钮的样子。不用主题里那套：那是给设置窗口的浅色按钮，
    /// 摆在这条深色条上看不见。
    /// 底色留给代码按选中状态直接设，模板里另铺一层悬停高亮，
    /// 这样鼠标划过选中的按钮时不会把高亮盖掉。
    /// </summary>
    static readonly Style BarBtnStyle = BuildBarBtnStyle();

    static Style BuildBarBtnStyle()
    {
        var hover = new FrameworkElementFactory(typeof(Border), "hover");
        hover.SetValue(Border.BackgroundProperty, Brushes.White);
        hover.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        hover.SetValue(UIElement.OpacityProperty, 0d);

        var back = new FrameworkElementFactory(typeof(Border));
        back.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        back.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding(nameof(Control.Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetBinding(FrameworkElement.MarginProperty,
            new System.Windows.Data.Binding(nameof(Control.Padding)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

        var root = new FrameworkElementFactory(typeof(Grid));
        root.AppendChild(back);
        root.AppendChild(hover);
        root.AppendChild(content);

        var tpl = new ControlTemplate(typeof(Button)) { VisualTree = root };
        tpl.Triggers.Add(new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true,
            Setters = { new Setter(UIElement.OpacityProperty, 0.16, "hover") },
        });
        tpl.Seal();

        var style = new Style(typeof(Button))
        {
            Setters =
            {
                new Setter(Control.TemplateProperty, tpl),
                new Setter(Control.BackgroundProperty, Brushes.Transparent),
                new Setter(Control.BorderThicknessProperty, new Thickness(0)),
                new Setter(Control.ForegroundProperty, BarText),
                new Setter(FrameworkElement.FocusVisualStyleProperty, null),
            },
        };
        style.Seal();
        return style;
    }

    /// <summary>
    /// 选区下面那条工具条。左边一组是画笔工具（互斥），右边一组是动作。
    /// 每个按钮的 ToolTip 都带快捷键，不用记也能看见。
    /// </summary>
    /// <summary>
    /// 「矩形 (R)」这种提示。键是可配的，所以从设置里读；配空了就只剩名字，
    /// 不留一对空括号。
    /// </summary>
    static string Tip(string name, string? key)
    {
        var k = HotkeySpec.Parse(key).ToString();
        return k.Length == 0 ? name : $"{name} ({k})";
    }

    Border BuildToolbar()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 0, 6, 0),
            Height = BarHeight,
        };

        foreach (var (tool, icon, tip) in new[]
                 {
                     (CaptureTool.Rect, IconRect, Tip("矩形", S.CkRect)),
                     (CaptureTool.Ellipse, IconEllipse, Tip("圆", S.CkEllipse)),
                     (CaptureTool.Arrow, IconArrow, Tip("箭头", S.CkArrow)),
                     (CaptureTool.Pen, IconPen, Tip("画笔", S.CkPen)),
                     (CaptureTool.Mosaic, IconMosaic, Tip("马赛克", S.CkMosaic)),
                     (CaptureTool.Text, null, Tip("文字", S.CkText)),
                 })
        {
            var b = icon is null
                ? ToolBtn("T", tip, () => ToggleTool(tool))
                : IconBtn(icon, tip, () => ToggleTool(tool));
            _toolButtons.Add((tool, b));
            row.Children.Add(b);
        }

        row.Children.Add(Sep());
        row.Children.Add(ColorPicker());
        row.Children.Add(Sep());

        _undoBtn = IconBtn(IconUndo, Tip("撤销", S.CkUndo), () => { _layer.Undo(); SyncToolbar(); });
        row.Children.Add(_undoBtn);
        _redoBtn = IconBtn(IconRedo, Tip("重做", S.CkRedo), () => { _layer.Redo(); SyncToolbar(); });
        row.Children.Add(_redoBtn);
        row.Children.Add(Sep());

        row.Children.Add(ActionBtn("长截图", Tip("往下滚动接着截", S.CkLongShot), FinishLongShot, IconLong));
        row.Children.Add(Sep());

        row.Children.Add(ActionBtn("识别文字", Tip("识别出来的字直接复制走", S.CkOcr),
            () => Finish(CaptureAction.Ocr)));
        row.Children.Add(ActionBtn("识别并翻译", Tip("识别成文字直接翻译", S.CkOcrTranslate),
            () => Finish(CaptureAction.OcrTranslate)));
        row.Children.Add(ActionBtn("保存", Tip("保存图片", S.CkSave), () => Finish(CaptureAction.Save)));
        row.Children.Add(ActionBtn("复制", Tip("复制到剪贴板", S.CkCopy) + " / 回车",
            () => Finish(CaptureAction.Copy)));
        // 叉画小一点：它的路径撑满整个框，跟旁边那些自带留白的图标并排会显得粗大一号
        row.Children.Add(IconBtn(IconCross, "取消 (Esc)", CloseOnce, size: 12));

        // 第二行：当前工具的参数。选了工具（或点中了一笔）才出来——
        // 什么都没选时它是空的，白占一条高度。
        _styleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 8, 0),
            Height = StyleRowHeight,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _styleRowHost = new Border
        {
            // 跟第一行之间画条线分开，不然两排按钮糊成一片
            BorderBrush = BarEdge,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _styleRow,
            Visibility = Visibility.Collapsed,
        };

        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(_styleRowHost);

        return new Border
        {
            Background = BarBack,
            BorderBrush = BarEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = stack,
        };
    }

    Button ToolBtn(string glyph, string tip, Action onClick) => MakeBtn(glyph, tip, onClick, 15, 30);

    /// <summary>
    /// 图标按钮。图标颜色跟着按钮前景走，选中时一起变白。
    /// size 是画出来多大：Stretch.Uniform 会把图形撑满这个框，所以图标之间的
    /// 视觉大小是靠这个数调的，改路径坐标没用。
    /// </summary>
    Button IconBtn(Geometry icon, string tip, Action onClick, double size = 15)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = icon,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var b = MakeBtn(path, tip, onClick, 15, 30);
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = b });
        return b;
    }

    /// <summary>动作按钮。icon 传了就在文字左边加个小图标。</summary>
    Button ActionBtn(string label, string tip, Action onClick, Geometry? icon = null)
    {
        if (icon is null) return MakeBtn(label, tip, onClick, 12.5, double.NaN);

        var path = new System.Windows.Shapes.Path
        {
            Data = icon,
            Stretch = Stretch.Uniform,
            Width = 13,
            Height = 13,
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(path);
        row.Children.Add(text);

        var b = MakeBtn(row, tip, onClick, 12.5, double.NaN);
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = b });
        return b;
    }

    Button MakeBtn(object content, string tip, Action onClick, double fontSize, double width)
    {
        var b = new Button
        {
            Content = content,
            ToolTip = tip,
            FontSize = fontSize,
            Foreground = BarText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = width,
            Height = 28,
            Padding = double.IsNaN(width) ? new Thickness(9, 0, 9, 0) : default,
            Margin = new Thickness(1, 0, 1, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI Symbol"),
            Style = BarBtnStyle,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    static UIElement Sep() => new Border
    {
        Width = 1,
        Height = 18,
        Background = BarEdge,
        Margin = new Thickness(5, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>颜色：八个小方块，点一下换。当前那个描个白边。</summary>
    UIElement ColorPicker()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var swatches = new List<(string Hex, Border Box)>();

        foreach (var hex in Palette)
        {
            var fill = new SolidColorBrush(ParseColor(hex));
            fill.Freeze();
            var box = new Border
            {
                Width = 14,
                Height = 14,
                Background = fill,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(2, 0, 0, 0),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = hex,
            };
            box.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _layer.PenColor = ParseColor(hex);
                S.CapturePenColor = hex;
                SettingsService.Instance.Save();
                // 顺手把刚画完的那一笔也改成这个颜色：画完才想换色是常事，
                // 不然得撤销重画。没画过东西时这句什么也不做。
                _layer.Restyle();
                // 正在打字时框里的字也跟着换色
                SyncTextBoxStyle();
                foreach (var (h, b) in swatches)
                    b.BorderBrush = h == hex ? Brushes.White : Brushes.Transparent;
            };
            swatches.Add((hex, box));
            row.Children.Add(box);
        }

        var cur = S.CapturePenColor;
        foreach (var (h, b) in swatches)
            if (string.Equals(h, cur, StringComparison.OrdinalIgnoreCase)) b.BorderBrush = Brushes.White;
        return row;
    }

    // ------------------------------------------------------------- 第二行：参数

    /// <summary>
    /// 第二行该摆什么。跟着选中的工具走；没选工具时跟着点中的那一笔走，
    /// 这样「点中刚写的字 → 调大」和「先调大 → 再写」都通。
    /// </summary>
    StyleCtx CurrentCtx()
    {
        var kind = _layer.Tool == CaptureTool.None ? _layer.ActiveKind : _layer.Tool;
        return kind switch
        {
            CaptureTool.Text => StyleCtx.Font,
            CaptureTool.Mosaic => StyleCtx.Mosaic,
            CaptureTool.None => StyleCtx.None,
            _ => StyleCtx.Width,
        };
    }

    /// <summary>
    /// 按当前上下文重建第二行。三种参数的控件不一样多，
    /// 挨个显示/隐藏比整行重建更啰嗦，而这行就几个按钮，重建一次不值一提。
    /// </summary>
    void RebuildStyleRow(StyleCtx ctx)
    {
        if (_styleRow is null || _styleRowHost is null) return;

        _styleCtx = ctx;
        _styleRow.Children.Clear();
        _styleChips = [];
        _styleValue = null;
        _boldBtn = null;
        _italicBtn = null;

        _styleRowHost.Visibility = ctx == StyleCtx.None ? Visibility.Collapsed : Visibility.Visible;
        if (ctx == StyleCtx.None) return;

        var (label, unit, presets) = ctx switch
        {
            StyleCtx.Font => ("字号", "px", CaptureLimits.FontSizes),
            StyleCtx.Mosaic => ("格子", "px", CaptureLimits.MosaicBlocks.Select(b => (double)b).ToArray()),
            _ => ("粗细", "px", CaptureLimits.PenWidths),
        };

        _styleRow.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = BarDim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
        });

        // 减 / 数值 / 加：一步一格的精调。滚轮也走同一条路。
        _styleRow.Children.Add(StepBtn("−", "小一点 (Ctrl+滚轮 向下)", () => Step(-1)));
        _styleValue = new TextBlock
        {
            FontSize = 11.5,
            Foreground = BarText,
            MinWidth = 34,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
        };
        _styleRow.Children.Add(_styleValue);
        _styleRow.Children.Add(StepBtn("+", "大一点 (Ctrl+滚轮 向上)", () => Step(1)));

        _styleRow.Children.Add(Sep());
        foreach (var v in presets)
        {
            var chip = Chip(Fmt(v), $"{Fmt(v)} {unit}", () => SetStyleValue(v));
            _styleChips.Add((v, chip));
            _styleRow.Children.Add(chip);
        }

        if (ctx != StyleCtx.Font) return;

        // 加粗和斜体只有文字用得上
        _styleRow.Children.Add(Sep());
        _boldBtn = ToggleBtn("B", "加粗 (Ctrl+B)", ToggleBold, FontWeights.Bold);
        _italicBtn = ToggleBtn("I", "斜体 (Ctrl+I)", ToggleItalic, FontWeights.Normal, italic: true);
        _styleRow.Children.Add(_boldBtn);
        _styleRow.Children.Add(_italicBtn);
    }

    /// <summary>整数就不带小数点：「3 px」比「3.0 px」好读。</summary>
    static string Fmt(double v) => v % 1 == 0 ? ((int)v).ToString() : v.ToString("0.#");

    /// <summary>第二行上那几个值跟着当前状态刷新。行本身不重建，只改显示。</summary>
    void RefreshStyleValues()
    {
        var cur = CurrentValue();
        if (_styleValue is not null) _styleValue.Text = Fmt(cur);

        foreach (var (v, chip) in _styleChips)
        {
            var on = Math.Abs(v - cur) < 0.01;
            chip.Background = on ? OnBack : Brushes.Transparent;
            chip.BorderBrush = on ? OnBack : BarEdge;
        }

        SetToggleOn(_boldBtn, _layer.TextBold);
        SetToggleOn(_italicBtn, _layer.TextItalic);
    }

    double CurrentValue() => _styleCtx switch
    {
        StyleCtx.Font => _layer.TextSize,
        StyleCtx.Mosaic => _layer.MosaicBlock,
        _ => _layer.PenWidth,
    };

    /// <summary>加减一格。字号和格子跨度大，一格走 2，粗细走 1。</summary>
    void Step(int dir)
    {
        var step = _styleCtx == StyleCtx.Width ? 1 : 2;
        SetStyleValue(CurrentValue() + dir * step);
    }

    /// <summary>把第二行那个值设成 v，落到设置里，并套到手头那一笔上。</summary>
    void SetStyleValue(double v)
    {
        switch (_styleCtx)
        {
            case StyleCtx.Font:
                _layer.TextSize = CaptureLimits.ClampFontSize(v);
                S.CaptureFontSize = _layer.TextSize;
                break;
            case StyleCtx.Mosaic:
                _layer.MosaicBlock = CaptureLimits.ClampMosaicBlock((int)Math.Round(v));
                S.CaptureMosaicBlock = _layer.MosaicBlock;
                break;
            default:
                _layer.PenWidth = CaptureLimits.ClampPenWidth(v);
                S.CapturePenWidth = _layer.PenWidth;
                break;
        }
        SettingsService.Instance.Save();
        // 跟颜色一样，改完的参数套到刚画完的那一笔上，不用撤销重画
        _layer.Restyle();
        SyncTextBoxStyle();
        RefreshStyleValues();
    }

    void ToggleBold()
    {
        _layer.TextBold = !_layer.TextBold;
        S.CaptureFontBold = _layer.TextBold;
        SettingsService.Instance.Save();
        _layer.Restyle();
        SyncTextBoxStyle();
        RefreshStyleValues();
    }

    void ToggleItalic()
    {
        _layer.TextItalic = !_layer.TextItalic;
        S.CaptureFontItalic = _layer.TextItalic;
        SettingsService.Instance.Save();
        _layer.Restyle();
        SyncTextBoxStyle();
        RefreshStyleValues();
    }

    /// <summary>加减按钮。方一点、小一点，跟旁边的预设块对得齐。</summary>
    Button StepBtn(string glyph, string tip, Action onClick)
    {
        var b = MakeBtn(glyph, tip, onClick, 13, 22);
        b.Height = 22;
        b.Margin = new Thickness(1, 0, 1, 0);
        return b;
    }

    /// <summary>预设值那一小块。当前那个填强调色。</summary>
    Border Chip(string text, string tip, Action onClick)
    {
        var box = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = BarEdge,
            Background = Brushes.Transparent,
            Margin = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 26,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = BarText,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
            },
        };
        box.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return box;
    }

    /// <summary>加粗/斜体那种开关按钮。字本身就按它代表的样式画，一眼看得出是什么。</summary>
    /// <summary>
    /// B / I 两个开关。字面用衬线体——无衬线的斜体大写 I 就是一根斜杠，
    /// 渲出来跟「/」分不清，这也是 Word 那一排为什么一直是衬线的。
    /// 衬线在同样字号下看着小一号，所以这里把字号提上去一点。
    /// </summary>
    Button ToggleBtn(string glyph, string tip, Action onClick, FontWeight weight, bool italic = false)
    {
        var b = MakeBtn(glyph, tip, onClick, 14.5, 24);
        b.Height = 22;
        b.FontFamily = new FontFamily("Times New Roman, Georgia, serif");
        b.FontWeight = weight;
        b.FontStyle = italic ? FontStyles.Italic : FontStyles.Normal;
        b.Margin = new Thickness(2, 0, 0, 0);
        return b;
    }

    static void SetToggleOn(Button? b, bool on)
    {
        if (b is null) return;
        b.Background = on ? OnBack : Brushes.Transparent;
        b.Foreground = on ? Brushes.White : BarText;
    }

    /// <summary>用不了的按钮压暗但留在原位——藏起来的话旁边那排会左右跳。</summary>
    static void Enable(Button? b, bool on)
    {
        if (b is null) return;
        b.IsEnabled = on;
        b.Opacity = on ? 1 : 0.35;
    }

    // ------------------------------------------------------------- 状态同步

    void ToggleTool(CaptureTool tool)
    {
        // 点已经选中的那个 = 收起来，回到能拖选区的状态
        _layer.Tool = _layer.Tool == tool ? CaptureTool.None : tool;
        CommitTextInput();
        SyncToolbar();
    }

    /// <summary>按钮的高亮和可用状态跟着层的状态走。</summary>
    void SyncToolbar()
    {
        foreach (var (tool, btn) in _toolButtons)
        {
            var on = _layer.Tool == tool;
            btn.Background = on ? OnBack : Brushes.Transparent;
            btn.Foreground = on ? Brushes.White : BarText;
        }
        Enable(_undoBtn, _layer.CanUndo);
        Enable(_redoBtn, _layer.CanRedo);
        _layer.Cursor = _layer.Tool == CaptureTool.None ? Cursors.Cross : Cursors.Pen;

        // 参数那一行换没换内容。换了要重新摆位置——行显出来或收起来时
        // 工具条会高一截或矮一截，原来那个 y 就可能把它顶到屏幕外面去。
        var ctx = CurrentCtx();
        if (ctx != _styleCtx)
        {
            RebuildStyleRow(ctx);
            PlaceToolbar();
        }
        RefreshStyleValues();
    }

    /// <summary>
    /// 工具条摆在选区正下方靠右。下面放不下就翻到上面，上下都放不下（选区几乎占满屏）
    /// 就压在选区里面的下沿——总之不能跑到屏幕外面去，那样按钮就点不到了。
    /// </summary>
    void OnSelectionChanged()
    {
        if (!_layer.HasSelection)
        {
            _toolbar.Visibility = Visibility.Collapsed;
            CommitTextInput();
            return;
        }

        _toolbar.Visibility = Visibility.Visible;
        SyncToolbar();
        PlaceToolbar();
    }

    /// <summary>
    /// 摆工具条。高度不是定值——第二行出来时它高一截，所以每次都现量一遍，
    /// 拿常量算的话参数行一出来就会把工具条顶出屏幕下沿。
    /// </summary>
    void PlaceToolbar()
    {
        if (!_layer.HasSelection) return;

        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = _toolbar.DesiredSize.Width;
        var h = _toolbar.DesiredSize.Height;
        var sel = _layer.Selection;

        var x = Math.Clamp(sel.Right - w, 0, Math.Max(0, _canvas.ActualWidth - w));
        var y = sel.Bottom + BarGap;
        if (y + h > _canvas.ActualHeight) y = sel.Top - h - BarGap;
        if (y < 0) y = Math.Max(0, Math.Min(sel.Bottom - h - BarGap, _canvas.ActualHeight - h));

        Canvas.SetLeft(_toolbar, x);
        Canvas.SetTop(_toolbar, y);
    }

    // ------------------------------------------------------------- 文字工具

    /// <summary>
    /// 文字工具点下去时在原地摆一个透明输入框。输完（Esc / 点别处 / 切工具）
    /// 才变成一条真正的标注。
    /// </summary>
    void StartTextInput(Point at)
    {
        CommitTextInput();

        var box = new TextBox
        {
            MinWidth = 60,
            MaxWidth = Math.Max(80, _layer.Selection.Right - at.X),
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
            Foreground = new SolidColorBrush(_layer.PenColor),
            CaretBrush = Brushes.White,
            BorderBrush = OnBack,
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            AcceptsReturn = false,
            Padding = new Thickness(2, 0, 2, 0),
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                e.Handled = true;
                CommitTextInput();
                _layer.Focus();
                return;
            }
            // 打字时焦点在输入框里，OnKey 直接放行，所以这两个键得在这儿再接一次——
            // 正在写的时候才最想加粗，写完再回头改反而绕。
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            if (e.Key == Key.B) { e.Handled = true; ToggleBold(); }
            else if (e.Key == Key.I) { e.Handled = true; ToggleItalic(); }
        };

        Canvas.SetLeft(box, at.X);
        Canvas.SetTop(box, at.Y);
        _canvas.Children.Add(box);
        _textBox = box;
        _textAt = at;
        SyncTextBoxStyle();
        box.Focus();
    }

    Point _textAt;

    /// <summary>
    /// 输入框的样子跟落到图上的一致，所见即所得。
    /// 调字号、加粗、换颜色时都得同步过来，否则框里是一号字、松手落下去是另一号。
    /// </summary>
    void SyncTextBoxStyle()
    {
        if (_textBox is null) return;
        _textBox.FontSize = _layer.TextSize;
        _textBox.FontWeight = _layer.TextBold ? FontWeights.Bold : FontWeights.Normal;
        _textBox.FontStyle = _layer.TextItalic ? FontStyles.Italic : FontStyles.Normal;
        _textBox.Foreground = new SolidColorBrush(_layer.PenColor);
    }

    /// <summary>把输入框里的字变成标注收掉。没输东西就直接扔。</summary>
    void CommitTextInput()
    {
        if (_textBox is null) return;
        var text = _textBox.Text;
        _canvas.Children.Remove(_textBox);
        _textBox = null;

        if (!string.IsNullOrWhiteSpace(text))
            _layer.AddAnnotation(new TextAnnotation
            {
                At = _textAt,
                Text = text,
                Color = _layer.PenColor,
                FontSize = _layer.TextSize,
                Bold = _layer.TextBold,
                Italic = _layer.TextItalic,
            });
        SyncToolbar();
    }

    // ------------------------------------------------------------- 键盘

    void OnKey(object sender, KeyEventArgs e)
    {
        // 焦点在输入框里时只让它自己处理，不然打个 R 就切成矩形工具了
        if (_textBox is not null && _textBox.IsKeyboardFocusWithin) return;

        // 先看固定的那几个。这些不给改：Esc 退出、回车确认、空格选窗口是
        // 到处都一样的约定，让它们可配置只会带来「把 Esc 设成别的键然后关不掉蒙层」这种麻烦。
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                // 有输入框先收输入框，有工具先退工具，都没有才关窗口——Esc 一层层往外退
                if (_textBox is not null) { CommitTextInput(); _layer.Focus(); }
                else if (_layer.Tool != CaptureTool.None) ToggleTool(_layer.Tool);
                else CloseOnce();
                return;
            case Key.Enter:
                e.Handled = true;
                Finish(S.CaptureEnterAction);
                return;
            case Key.Space:
                e.Handled = true;
                SelectWindowUnderCursor();
                return;
        }

        // Ctrl+A 全选。放在可配置的键之前：它跟「A = 箭头」不冲突（带 Ctrl），
        // 但如果用户把某个动作也设成了 Ctrl+A，先到先得，全选优先。
        if (Matches(e, ModifierKeys.Control, Key.A))
        {
            e.Handled = true;
            _layer.SelectAll();
            OnSelectionChanged();
            return;
        }

        // Ctrl+Shift+Z 也是重做。可配置的那个默认是 Ctrl+Y，但不少人手先按的是这个，
        // 两个都收下。放在 KeyMap 之前：Matches 要求修饰键完全相等，撞不到 Ctrl+Z。
        if (Matches(e, ModifierKeys.Control | ModifierKeys.Shift, Key.Z))
        {
            e.Handled = true;
            _layer.Redo();
            SyncToolbar();
            return;
        }

        // Ctrl+B / Ctrl+I：文字的加粗和斜体，跟别处一样的约定。
        // 只在第二行摆着字号时才认，其它工具下留给可配置的键。
        if (_styleCtx == StyleCtx.Font && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.B) { e.Handled = true; ToggleBold(); return; }
            if (e.Key == Key.I) { e.Handled = true; ToggleItalic(); return; }
        }

        // 方向键微调选中那一笔的位置。鼠标拖是粗调，差一两个像素时用键。
        // 按住 Shift 走 10 个像素。没选中东西时不拦，留给别的键。
        if (Nudge(e.Key) is { } step)
        {
            var far = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (_layer.Nudge(step * (far ? 10 : 1))) { e.Handled = true; return; }
        }

        // Delete 删掉选中那一笔。撤销砍的是最后画的，这个砍的是点中的。
        if (e.Key is Key.Delete or Key.Back && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (_layer.DeleteActive()) { e.Handled = true; SyncToolbar(); return; }
        }

        foreach (var (spec, run) in KeyMap())
        {
            if (!Matches(e, spec.Modifiers, spec.Key)) continue;
            e.Handled = true;
            run();
            return;
        }
    }

    /// <summary>
    /// 设置里配的那些键 → 干什么。顺序即优先级，同一个键配给两处时前面的赢。
    /// 每次按键都重建一遍：十来个字符串解析，比起用户改了设置要同步过来的麻烦不值一提。
    /// </summary>
    IEnumerable<(HotkeySpec Spec, Action Run)> KeyMap()
    {
        yield return (HotkeySpec.Parse(S.CkUndo), () => { _layer.Undo(); SyncToolbar(); });
        yield return (HotkeySpec.Parse(S.CkRedo), () => { _layer.Redo(); SyncToolbar(); });
        yield return (HotkeySpec.Parse(S.CkCopy), () => Finish(CaptureAction.Copy));
        yield return (HotkeySpec.Parse(S.CkSave), () => Finish(CaptureAction.Save));
        // 识别并翻译排在识别前面：默认它们是 Ctrl+Shift+D 和 Ctrl+D，
        // 而 Matches 要求修饰键完全相等，所以其实不会误判；顺序在这儿只是为了
        // 万一有人把两个都设成同一个键时，行为是确定的。
        yield return (HotkeySpec.Parse(S.CkOcrTranslate), () => Finish(CaptureAction.OcrTranslate));
        yield return (HotkeySpec.Parse(S.CkOcr), () => Finish(CaptureAction.Ocr));
        yield return (HotkeySpec.Parse(S.CkLongShot), FinishLongShot);
        yield return (HotkeySpec.Parse(S.CkRect), () => ToggleTool(CaptureTool.Rect));
        yield return (HotkeySpec.Parse(S.CkEllipse), () => ToggleTool(CaptureTool.Ellipse));
        yield return (HotkeySpec.Parse(S.CkArrow), () => ToggleTool(CaptureTool.Arrow));
        yield return (HotkeySpec.Parse(S.CkPen), () => ToggleTool(CaptureTool.Pen));
        yield return (HotkeySpec.Parse(S.CkMosaic), () => ToggleTool(CaptureTool.Mosaic));
        yield return (HotkeySpec.Parse(S.CkText), () => ToggleTool(CaptureTool.Text));
    }

    /// <summary>方向键 → 挪一个像素的方向。不是方向键就返回 null。</summary>
    static Vector? Nudge(Key key) => key switch
    {
        Key.Left => new Vector(-1, 0),
        Key.Right => new Vector(1, 0),
        Key.Up => new Vector(0, -1),
        Key.Down => new Vector(0, 1),
        _ => null,
    };

    /// <summary>
    /// 这一下按键是不是那个组合。修饰键要求完全相等，不是「包含」——
    /// 否则 Ctrl+Shift+D 会先被 Ctrl+D 吃掉。
    /// </summary>
    static bool Matches(KeyEventArgs e, ModifierKeys mods, Key key)
        => key != Key.None && e.Key == key && Keyboard.Modifiers == mods;

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        // Ctrl+滚轮调第二行上那个参数：选着文字工具就调字号，马赛克就调格子，
        // 其余调粗细。手不用离开画的地方。
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (_styleCtx == StyleCtx.None) return;
        e.Handled = true;
        Step(e.Delta > 0 ? 1 : -1);
    }

    /// <summary>
    /// 空格：把选区套到鼠标底下那个窗口上。
    /// 用的是弹蒙层之前存下来的窗口列表——蒙层自己盖满了屏幕，
    /// 这时候现问 WindowFromPoint 只会拿到蒙层。
    /// </summary>
    void SelectWindowUnderCursor()
    {
        if (!Win32.GetCursorPos(out var pt)) return;

        // 列表是从上往下的，第一个套住光标的就是用户看见的那个
        foreach (var r in _windows)
        {
            if (pt.X < r.Left || pt.X >= r.Right || pt.Y < r.Top || pt.Y >= r.Bottom) continue;

            // 屏幕像素 → 层的 DIP
            var sx = _shot.Width > 0 ? _layer.ActualWidth / _shot.Width : 1;
            var sy = _shot.Height > 0 ? _layer.ActualHeight / _shot.Height : 1;
            var rect = new Rect(
                (r.Left - _screen.Left) * sx, (r.Top - _screen.Top) * sy,
                (r.Right - r.Left) * sx, (r.Bottom - r.Top) * sy);

            // 最大化的窗口边框会超出屏幕，夹回来
            rect.Intersect(new Rect(0, 0, _layer.ActualWidth, _layer.ActualHeight));
            if (rect.Width < 8 || rect.Height < 8) continue;

            _layer.PresetSelection(rect);
            OnSelectionChanged();
            return;
        }
    }

    /// <summary>
    /// 当前看得见的顶层窗口，按 Z 序从上到下。
    /// 必须在蒙层显示之前调用，不然列表里第一个就是蒙层自己。
    /// </summary>
    static List<RECT> TopLevelWindows()
    {
        var list = new List<RECT>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return true;
            // 工具窗口是任务栏图标、输入法候选框那类，不是用户想截的「窗口」
            if ((Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0) return true;
            if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
                && cloaked != 0) return true;
            if (!Win32.GetWindowRect(hwnd, out var r)) return true;
            if (r.Right - r.Left < 8 || r.Bottom - r.Top < 8) return true;

            list.Add(r);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    /// <summary>
    /// 单独造一条工具条来渲染，不弹窗——给自测截图用。
    /// 这条东西上有十几个按钮挤在一起，字号、间距、选中高亮对不对，
    /// 只有渲成图才看得出来。
    /// </summary>
    internal static FrameworkElement ToolbarForShot(
        CaptureTool tool = CaptureTool.None, bool bold = false, bool italic = false)
    {
        // 底图用不着，随便给一小块；构造函数只拿它建选区层
        var win = new CaptureOverlay(new CapturedImage(8, 8, new byte[8 * 8 * 4]), default);
        win._layer.Tool = tool;
        // B / I 按下去的样子也得看一眼：亮起来之后那个字面还认不认得出
        win._layer.TextBold = bold;
        win._layer.TextItalic = italic;

        // 走一遍真正的排版。单独对工具条 Measure 是没用的：它挂在一个从没显示过的
        // 窗口下面，那种状态下量出来永远是 0。
        var size = new Size(900, 500);
        win._canvas.Measure(size);
        win._canvas.Arrange(new Rect(size));
        win._layer.Width = size.Width;
        win._layer.Height = size.Height;
        win._layer.PresetSelection(new Rect(100, 80, 600, 300));
        win.OnSelectionChanged();
        win._canvas.UpdateLayout();
        return win._toolbar;
    }
}
