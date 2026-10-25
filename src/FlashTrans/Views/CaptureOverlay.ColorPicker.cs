using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlashTrans.Views;

public sealed partial class CaptureOverlay
{
    Border? _colorPreview;
    Border? _colorSwatch;
    TextBlock? _colorValues;
    TextBlock? _colorHelp;

    void InitializeColorPicker()
    {
        _layer.ColorHovered += ShowColorPreview;
        _layer.ColorPicked += at => CopySampledColor(at, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        _layer.ColorPickingCancelled += () => ToggleTool(CaptureTool.ColorPicker);
        _layer.MouseLeave += (_, _) => HideColorPreview();
    }

    internal static string FormatSampledColor(Color color, bool rgb) => rgb
        ? $"rgb({color.R}, {color.G}, {color.B})"
        : $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    void ShowColorPreview(Point at)
    {
        if (!_layer.TrySampleColor(at, out var color, out var pixel)) { HideColorPreview(); return; }
        if (_colorPreview is null)
        {
            _colorSwatch = new Border
            {
                Width = 36, Height = 36, BorderBrush = BarText, BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 10, 0),
            };
            _colorValues = new TextBlock { Foreground = BarText, FontSize = 12, FontFamily = new FontFamily("Consolas") };
            _colorHelp = new TextBlock { Foreground = BarDim, FontSize = 11, Margin = new Thickness(0, 7, 0, 0) };
            var values = new StackPanel { Orientation = Orientation.Horizontal };
            values.Children.Add(_colorSwatch);
            values.Children.Add(_colorValues);
            var panel = new StackPanel();
            panel.Children.Add(values);
            panel.Children.Add(_colorHelp);
            _colorPreview = new Border
            {
                Child = panel, Background = BarBack, BorderBrush = BarEdge, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(10), IsHitTestVisible = false,
            };
            Panel.SetZIndex(_colorPreview, 100);
            _canvas.Children.Add(_colorPreview);
        }

        _colorSwatch!.Background = new SolidColorBrush(color);
        _colorValues!.Text = $"{FormatSampledColor(color, false)}\n{FormatSampledColor(color, true)}\n像素 {pixel.X:0}, {pixel.Y:0}";
        _colorHelp!.Text = "单击复制 HEX · Shift+单击复制 RGB\n原图取色 · 右键 / Esc 返回";
        _colorPreview.Visibility = Visibility.Visible;
        _colorPreview.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _colorPreview.DesiredSize;
        var x = at.X + 20;
        var y = at.Y + 20;
        if (x + size.Width > _canvas.ActualWidth) x = at.X - size.Width - 20;
        if (y + size.Height > _canvas.ActualHeight) y = at.Y - size.Height - 20;
        Canvas.SetLeft(_colorPreview, Math.Clamp(x, 0, Math.Max(0, _canvas.ActualWidth - size.Width)));
        Canvas.SetTop(_colorPreview, Math.Clamp(y, 0, Math.Max(0, _canvas.ActualHeight - size.Height)));
    }

    void CopySampledColor(Point at, bool rgb)
    {
        if (!_layer.TrySampleColor(at, out var color, out _)) return;
        ShowColorPreview(at);
        var text = FormatSampledColor(color, rgb);
        try
        {
            // 用能报告失败的剪贴板接口，不能在被其他程序占用时仍显示“已复制”。
            Clipboard.SetDataObject(text, true);
            _colorHelp!.Text = $"已复制 {text}\n可继续取色 · 右键 / Esc 返回";
        }
        catch (Exception)
        {
            _colorHelp!.Text = "剪贴板暂时不可用，请再次单击重试";
        }
    }

    void HideColorPreview()
    {
        if (_colorPreview is not null) _colorPreview.Visibility = Visibility.Collapsed;
    }
}
