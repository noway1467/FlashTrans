using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>设置页里反复用到的表单控件工厂，保证各页排版一致。</summary>
public sealed partial class SettingsWindow
{
    static StackPanel Page() => new() { Margin = new Thickness(0, 0, 2, 0) };

    /// <summary>一个分组：标题 + 卡片。</summary>
    static void Section(Panel host, string title, params UIElement[] rows)
    {
        var head = UiKit.Text(title, 12, "TextDim", FontWeights.SemiBold);
        head.Margin = new Thickness(2, host.Children.Count == 0 ? 0 : 14, 0, 6);
        host.Children.Add(head);

        var stack = new StackPanel();
        foreach (var r in rows)
        {
            if (r is FrameworkElement fe && stack.Children.Count > 0)
                fe.Margin = new Thickness(fe.Margin.Left, 9, fe.Margin.Right, fe.Margin.Bottom);
            stack.Children.Add(r);
        }
        host.Children.Add(UiKit.Card(stack, new Thickness(12, 11, 12, 12)));
    }

    /// <summary>左标签 + 右控件的一行。</summary>
    static UIElement Field(string label, UIElement control, string? hint = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(UiKit.Text(label, 12.5, "Text"));
        if (hint is not null)
        {
            var h = UiKit.Text(hint, 10.5, "TextFaint", wrap: true);
            h.Margin = new Thickness(0, 2, 8, 0);
            text.Children.Add(h);
        }
        UiKit.SetGrid(text, col: 0);
        grid.Children.Add(text);

        if (control is FrameworkElement c) c.HorizontalAlignment = HorizontalAlignment.Stretch;
        UiKit.SetGrid(control, col: 1);
        grid.Children.Add(control);
        return grid;
    }

    static CheckBox Check(string label, bool value, Action<bool> onChange, string? hint = null)
    {
        var box = new CheckBox { IsChecked = value, FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center };
        if (hint is null) box.Content = label;
        else
        {
            var sp = new StackPanel();
            sp.Children.Add(UiKit.Text(label, 12.5));
            var h = UiKit.Text(hint, 10.5, "TextFaint", wrap: true);
            h.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(h);
            box.Content = sp;
        }
        box.Checked += (_, _) => { onChange(true); Save(); };
        box.Unchecked += (_, _) => { onChange(false); Save(); };
        return box;
    }

    static TextBox Input(string value, Action<string> onChange, string? placeholder = null,
                         bool secret = false, double width = double.NaN)
    {
        var box = new TextBox
        {
            Text = value,
            FontSize = 12.5,
            Height = 28,
            Padding = new Thickness(7, 0, 7, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Width = width,
        };
        if (secret)
        {
            box.FontFamily = new FontFamily("Consolas, Courier New");
            box.ToolTip = "密钥保存时会用 Windows DPAPI 加密";
        }
        if (placeholder is not null) box.Tag = placeholder;

        box.TextChanged += (_, _) => onChange(box.Text);
        box.LostFocus += (_, _) => Save();
        return box;
    }

    static UIElement Number(int value, int min, int max, Action<int> onChange, string? suffix = null)
    {
        var box = new TextBox
        {
            Text = value.ToString(CultureInfo.InvariantCulture),
            FontSize = 12.5,
            Height = 28,
            Width = 86,
            Padding = new Thickness(7, 0, 7, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, out var v)) onChange(Math.Clamp(v, min, max));
        };
        box.LostFocus += (_, _) =>
        {
            if (!int.TryParse(box.Text, out var v)) v = value;
            v = Math.Clamp(v, min, max);
            box.Text = v.ToString(CultureInfo.InvariantCulture);
            onChange(v);
            Save();
        };

        if (suffix is null) return box;
        var row = UiKit.Row(6, box, UiKit.Text(suffix, 11, "TextFaint"));
        row.HorizontalAlignment = HorizontalAlignment.Left;
        return row;
    }

    static UIElement SliderRow(double value, double min, double max, double tick,
                               Action<double> onChange, Func<double, string> format)
    {
        var label = UiKit.Text(format(value), 11.5, "TextDim");
        label.MinWidth = 52;
        label.TextAlignment = TextAlignment.Right;

        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value,
            TickFrequency = tick, IsSnapToTickEnabled = true,
            Width = 200, VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            label.Text = format(e.NewValue);
            onChange(e.NewValue);
        };
        slider.PreviewMouseLeftButtonUp += (_, _) => Save();
        slider.LostFocus += (_, _) => Save();

        var row = UiKit.Row(10, slider, label);
        row.HorizontalAlignment = HorizontalAlignment.Left;
        return row;
    }

    static ComboBox Combo<T>(IEnumerable<(string Label, T Value)> items, T current, Action<T> onChange,
                             double width = 190)
    {
        var box = new ComboBox
        {
            FontSize = 12.5,
            Height = 28,
            Width = width,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var list = items.ToList();
        foreach (var (label, value) in list)
            box.Items.Add(new ComboBoxItem { Content = label, Tag = value, FontSize = 12.5 });

        var idx = list.FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, current));
        box.SelectedIndex = idx < 0 ? 0 : idx;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: T v }) { onChange(v); Save(); }
        };
        return box;
    }

    static Button SmallButton(string label, Action onClick, string style = "OutlineBtn")
    {
        var btn = new Button
        {
            Content = label,
            FontSize = 11.5,
            Height = 26,
            Padding = new Thickness(11, 0, 11, 0),
        };
        btn.SetResourceReference(StyleProperty, style);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    static UIElement Hint(string text)
    {
        var tb = UiKit.Text(text, 10.5, "TextFaint", wrap: true);
        tb.Margin = new Thickness(2, 6, 2, 0);
        return tb;
    }

    static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Log.Warn("打开链接失败：" + ex.Message); }
    }
}
