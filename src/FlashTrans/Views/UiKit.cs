using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FlashTrans.Views;

/// <summary>矢量图标 + 常用控件工厂，避免依赖图标字体。</summary>
public static class UiKit
{
    // 图标统一按 16x16 设计
    public static readonly Geometry IconCopy = Geometry.Parse(
        "M5.5,2 H12 A1.5,1.5 0 0 1 13.5,3.5 V10 A1.5,1.5 0 0 1 12,11.5 H5.5 A1.5,1.5 0 0 1 4,10 V3.5 " +
        "A1.5,1.5 0 0 1 5.5,2 Z M4,5 H3.2 A1.2,1.2 0 0 0 2,6.2 V12.8 A1.2,1.2 0 0 0 3.2,14 H9.8 " +
        "A1.2,1.2 0 0 0 11,12.8 V11.5");
    public static readonly Geometry IconSwap = Geometry.Parse(
        "M2,5.5 H12 M9.5,3 L12,5.5 L9.5,8 M14,10.5 H4 M6.5,8 L4,10.5 L6.5,13");
    public static readonly Geometry IconSettings = Geometry.Parse(
        "M2,4 H14 M2,8 H14 M2,12 H14 M5.5,4 A1.4,1.4 0 1 0 5.5,4.01 Z " +
        "M10.5,8 A1.4,1.4 0 1 0 10.5,8.01 Z M6.5,12 A1.4,1.4 0 1 0 6.5,12.01 Z");
    public static readonly Geometry IconPin = Geometry.Parse(
        "M6,2 H10 L9.4,8 L12,10.5 H4 L6.6,8 Z M8,10.5 V14");
    public static readonly Geometry IconClose = Geometry.Parse("M3.5,3.5 L12.5,12.5 M12.5,3.5 L3.5,12.5");
    public static readonly Geometry IconMinimize = Geometry.Parse("M3.5,8 H12.5");
    public static readonly Geometry IconSearch = Geometry.Parse(
        "M7,2 A5,5 0 1 1 6.99,2 Z M10.6,10.6 L14,14");
    public static readonly Geometry IconStar = Geometry.Parse(
        "M8,2 L9.9,6.1 L14,6.7 L11,9.7 L11.8,14 L8,11.9 L4.2,14 L5,9.7 L2,6.7 L6.1,6.1 Z");
    public static readonly Geometry IconExpand = Geometry.Parse(
        "M9.5,2.5 H13.5 V6.5 M13.5,2.5 L8.5,7.5 M6.5,13.5 H2.5 V9.5 M2.5,13.5 L7.5,8.5");
    public static readonly Geometry IconBook = Geometry.Parse(
        "M2.5,3 H7 A1,1 0 0 1 8,4 V13 A1,1 0 0 0 7,12 H2.5 Z M13.5,3 H9 A1,1 0 0 0 8,4 V13 " +
        "A1,1 0 0 1 9,12 H13.5 Z");
    public static readonly Geometry IconRefresh = Geometry.Parse(
        "M13.5,8 A5.5,5.5 0 1 1 11.6,3.8 M13.8,1.8 V4.4 H11.2");
    public static readonly Geometry IconPlus = Geometry.Parse("M8,3 V13 M3,8 H13");
    public static readonly Geometry IconTrash = Geometry.Parse(
        "M3,4.5 H13 M6.5,4.5 V2.8 H9.5 V4.5 M4.3,4.5 L5,13.2 H11 L11.7,4.5 M6.7,7 V11 M9.3,7 V11");
    public static readonly Geometry IconUp = Geometry.Parse("M4,10 L8,5.5 L12,10");
    public static readonly Geometry IconDown = Geometry.Parse("M4,6 L8,10.5 L12,6");
    public static readonly Geometry IconCheck = Geometry.Parse("M3,8.5 L6.5,12 L13,4.5");
    public static readonly Geometry IconWarn = Geometry.Parse(
        "M8,1.8 L15,13.8 H1 Z M8,6 V9.9 M8,11.6 V11.9");
    public static readonly Geometry IconInfo = Geometry.Parse(
        "M8,1.5 A6.5,6.5 0 1 1 7.99,1.5 Z M8,7 V11.5 M8,4.4 V4.7");
    public static readonly Geometry IconGlobe = Geometry.Parse(
        "M8,1.5 A6.5,6.5 0 1 1 7.99,1.5 Z M1.5,8 H14.5 M8,1.5 C5.6,4 5.6,12 8,14.5 " +
        "C10.4,12 10.4,4 8,1.5");

    public static Path Icon(Geometry data, double size = 13, Brush? stroke = null,
                           double thickness = 1.35, bool fill = false)
    {
        var p = new Path
        {
            Data = data,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        if (fill) p.SetResourceReference(Shape.FillProperty, "TextDim");
        else if (stroke is not null) p.Stroke = stroke;
        else p.SetResourceReference(Shape.StrokeProperty, "TextDim");
        return p;
    }

    public static Button IconButton(Geometry data, string tooltip, RoutedEventHandler onClick,
                                    double size = 13, string style = "IconBtn")
    {
        var btn = new Button
        {
            Content = Icon(data, size),
            ToolTip = tooltip,
            Focusable = false,
        };
        btn.SetResourceReference(FrameworkElement.StyleProperty, style);
        btn.Click += onClick;
        // 图标颜色跟随按钮前景
        if (btn.Content is Path p)
            p.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) }
            });
        return btn;
    }

    public static TextBlock Text(string text, double size = 13, string brush = "Text",
                                 FontWeight? weight = null, bool wrap = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (weight is { } w) tb.FontWeight = w;
        tb.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return tb;
    }

    /// <summary>可选中复制的译文文本框（去掉输入框外观）。</summary>
    public static TextBox SelectableText(string text, double size)
    {
        var tb = new TextBox
        {
            Text = text,
            FontSize = size,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            IsReadOnlyCaretVisible = false,
        };
        tb.SetResourceReference(FrameworkElement.StyleProperty, "ReadOnlyText");
        return tb;
    }

    /// <summary>翻译源色块徽标。</summary>
    public static Border Badge(string label, string accentHex)
    {
        var color = Services.ThemeService.Parse(accentHex, Color.FromRgb(0x4C, 0x8D, 0xFF));
        return new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = label.Length <= 2 ? label : label[..2],
                FontSize = label.Length > 1 ? 9.5 : 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
    }

    public static Ellipse StatusDot(bool ok)
    {
        var dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, ok ? "Success" : "Danger");
        return dot;
    }

    public static Border Card(UIElement child, Thickness? padding = null)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = padding ?? new Thickness(10, 8, 10, 9),
            Child = child,
            SnapsToDevicePixels = true,
        };
        b.SetResourceReference(Border.BackgroundProperty, "BgCard");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.BorderThickness = new Thickness(1);
        return b;
    }

    public static StackPanel Row(double spacing = 6, params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < children.Length; i++)
        {
            if (i > 0 && children[i] is FrameworkElement fe)
                fe.Margin = new Thickness(spacing, 0, 0, 0);
            p.Children.Add(children[i]);
        }
        return p;
    }

    public static void SetGrid(UIElement el, int row = 0, int col = 0, int rowSpan = 1, int colSpan = 1)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        if (rowSpan > 1) Grid.SetRowSpan(el, rowSpan);
        if (colSpan > 1) Grid.SetColumnSpan(el, colSpan);
    }
}
