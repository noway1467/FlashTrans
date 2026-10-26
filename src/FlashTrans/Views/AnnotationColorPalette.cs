using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FlashTrans.Views;

/// <summary>设置与截图共用的不透明标注色板；确认前不改动当前标注。</summary>
internal sealed class AnnotationColorPalette : Border
{
    internal static readonly string[] Presets =
        ["#FF3B30", "#FF9500", "#FFCC00", "#34C759", "#4C8DFF", "#AF52DE", "#FFFFFF", "#1C1C1E"];

    readonly Grid _surface = new() { Name = "ColorSurface", Height = 132, Focusable = true, Cursor = Cursors.Cross };
    readonly Canvas _markerLayer = new() { IsHitTestVisible = false };
    readonly Ellipse _marker = new() { Width = 10, Height = 10, Stroke = Brushes.White,
        StrokeThickness = 2, Fill = Brushes.Transparent };
    readonly Slider _hue = new() { Name = "ColorHue", Minimum = 0, Maximum = 360, Height = 22,
        SmallChange = 1, LargeChange = 30, ToolTip = "色相" };
    readonly TextBox _hex = new() { Name = "ColorHex", MaxLength = 7, Height = 30, Width = 112,
        FontFamily = new FontFamily("Consolas"), FontSize = 12.5, Padding = new Thickness(6, 0, 6, 0),
        VerticalContentAlignment = VerticalAlignment.Center };
    readonly Border _preview = new() { Width = 30, Height = 30, CornerRadius = new CornerRadius(5),
        BorderThickness = new Thickness(1) };
    readonly TextBlock _error = UiKit.Text("", 11, "Danger");
    double _saturation;
    double _value;
    bool _updating;
    public event Action<Color>? Applied;
    public event Action? Cancelled;

    internal AnnotationColorPalette(Color initial, bool dark = false)
    {
        Width = 280;
        Padding = new Thickness(14);
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        if (dark)
        {
            // 截图工具条始终为深色，不跟随设置页的浅色主题。
            foreach (var (key, hex) in new[] { ("BgCard", "#26282C"), ("BgInput", "#2B2D31"),
                         ("Text", "#E8EAED"), ("TextDim", "#A6ACB6"), ("Border", "#4C5158") })
                Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        SetResourceReference(BackgroundProperty, "BgCard");
        SetResourceReference(BorderBrushProperty, "Border");
        var content = new StackPanel();
        content.Children.Add(UiKit.Text("标注颜色", 13, "Text", FontWeights.SemiBold));
        var presets = new UniformGrid { Columns = Presets.Length, Margin = new Thickness(0, 10, 0, 10) };
        foreach (var hex in Presets)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var chip = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(color), BorderThickness = new Thickness(1) };
            chip.SetResourceReference(BorderBrushProperty, "Border");
            var button = new Button { Content = chip, ToolTip = hex, Padding = new Thickness(3), Height = 28 };
            button.SetResourceReference(StyleProperty, "GhostBtn");
            System.Windows.Automation.AutomationProperties.SetName(button, hex);
            button.Click += (_, _) => SetColor(color);
            presets.Children.Add(button);
        }
        content.Children.Add(presets);

        _surface.Children.Add(new Border { Background = new LinearGradientBrush(Colors.White, Colors.Transparent, 0) });
        _surface.Children.Add(new Border { Background = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90) });
        _markerLayer.Children.Add(_marker);
        _surface.Children.Add(_markerLayer);
        _surface.ToolTip = "拖动选择颜色；方向键调整饱和度和明度";
        System.Windows.Automation.AutomationProperties.SetName(_surface, "饱和度和明度");
        _surface.SizeChanged += (_, _) => UpdateMarker();
        _surface.MouseLeftButtonDown += (_, e) =>
        {
            _surface.Focus(); _surface.CaptureMouse(); SelectAt(e.GetPosition(_surface)); e.Handled = true;
        };
        _surface.MouseMove += (_, e) =>
        {
            if (_surface.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) SelectAt(e.GetPosition(_surface));
        };
        _surface.MouseLeftButtonUp += (_, e) =>
        {
            if (_surface.IsMouseCaptured) { SelectAt(e.GetPosition(_surface)); _surface.ReleaseMouseCapture(); }
            e.Handled = true;
        };
        _surface.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
            _saturation = Math.Clamp(_saturation + (e.Key == Key.Right ? .01 : e.Key == Key.Left ? -.01 : 0), 0, 1);
            _value = Math.Clamp(_value + (e.Key == Key.Up ? .01 : e.Key == Key.Down ? -.01 : 0), 0, 1);
            RefreshColor(); e.Handled = true;
        };
        content.Children.Add(_surface);
        var spectrum = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        for (var i = 0; i <= 6; i++) spectrum.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6d));
        content.Children.Add(new Border { Height = 7, Background = spectrum, CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 10, 0, 0) });
        _hue.ValueChanged += (_, _) => { if (!_updating) RefreshColor(); };
        System.Windows.Automation.AutomationProperties.SetName(_hue, "色相");
        content.Children.Add(_hue);
        _preview.SetResourceReference(BorderBrushProperty, "Border");
        _hex.ToolTip = "输入 #RRGGBB，例如 #18A7B5";
        System.Windows.Automation.AutomationProperties.SetName(_hex, "HEX 颜色");
        _hex.TextChanged += (_, _) =>
        {
            if (_updating) return;
            if (TryParseHex(_hex.Text, out var color)) { SetColor(color, updateHex: false); _error.Text = ""; }
            else _error.Text = "请输入 6 位 HEX 颜色";
        };
        content.Children.Add(UiKit.Row(8, _preview, UiKit.Text("HEX", 11, "TextDim"), _hex));
        _error.Height = 20;
        content.Children.Add(_error);
        var apply = new Button { Name = "ApplyColor", Content = "使用此颜色", Height = 30, Padding = new Thickness(14, 0, 14, 0) };
        apply.SetResourceReference(StyleProperty, "PrimaryBtn");
        apply.Click += (_, _) => Apply();
        var cancel = new Button { Content = "取消", Height = 30, Padding = new Thickness(12, 0, 12, 0) };
        cancel.SetResourceReference(StyleProperty, "GhostBtn");
        cancel.Click += (_, _) => Cancelled?.Invoke();
        content.Children.Add(UiKit.Row(8, apply, cancel));
        Child = content;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Cancelled?.Invoke(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Apply(); e.Handled = true; }
        };
        SetColor(initial);
    }

    void Apply()
    {
        if (TryParseHex(_hex.Text, out var color)) Applied?.Invoke(color);
        else { _error.Text = "请输入 6 位 HEX 颜色"; _hex.Focus(); }
    }

    void SelectAt(Point point)
    {
        _saturation = Math.Clamp(point.X / Math.Max(1, _surface.ActualWidth), 0, 1);
        _value = 1 - Math.Clamp(point.Y / Math.Max(1, _surface.ActualHeight), 0, 1);
        RefreshColor();
    }

    void SetColor(Color color, bool updateHex = true)
    {
        var r = color.R / 255d; var g = color.G / 255d; var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        _updating = true;
        if (delta > 0)
            _hue.Value = ((max == r ? (g - b) / delta : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4) * 60 + 360) % 360;
        _updating = false;
        _saturation = max == 0 ? 0 : delta / max;
        _value = max;
        RefreshColor(updateHex);
    }

    void RefreshColor(bool updateHex = true)
    {
        var color = FromHsv(_hue.Value, _saturation, _value);
        _surface.Background = new SolidColorBrush(FromHsv(_hue.Value, 1, 1));
        _preview.Background = new SolidColorBrush(color);
        if (updateHex)
        {
            _updating = true; _hex.Text = ToHex(color); _updating = false;
            _error.Text = "";
        }
        UpdateMarker();
    }

    void UpdateMarker()
    {
        Canvas.SetLeft(_marker, _saturation * _surface.ActualWidth - 5);
        Canvas.SetTop(_marker, (1 - _value) * _surface.ActualHeight - 5);
    }

    internal static Color FromHsv(double hue, double saturation, double value)
    {
        var h = (hue % 360 + 360) % 360 / 60;
        var c = value * saturation; var x = c * (1 - Math.Abs(h % 2 - 1)); var m = value - c;
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x),
            3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    internal static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    internal static bool TryParseHex(string text, out Color color)
    {
        var hex = text.Trim();
        if (hex.StartsWith('#')) hex = hex[1..];
        color = default;
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    internal static Popup Attach(Button target, Func<Color> current, Action<Color> apply, bool dark = false)
    {
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, VerticalOffset = 6,
            AllowsTransparency = true, StaysOpen = false };
        target.Click += (_, _) =>
        {
            if (popup.IsOpen) { popup.IsOpen = false; return; }
            var palette = new AnnotationColorPalette(current(), dark);
            palette.Applied += color => { apply(color); popup.IsOpen = false; };
            palette.Cancelled += () => popup.IsOpen = false;
            popup.Child = palette;
            popup.IsOpen = true;
            palette._hex.Focus();
            palette._hex.SelectAll();
        };
        target.Unloaded += (_, _) => popup.IsOpen = false;
        return popup;
    }
}
