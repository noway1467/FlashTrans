using System.Windows;
using System.Windows.Media;
using FlashTrans.Core;

namespace FlashTrans.Services;

/// <summary>主题与强调色切换：替换合并字典的第一项，其余控件样式通过 DynamicResource 自动跟随。</summary>
public static class ThemeService
{
    static AppTheme _current = AppTheme.Dark;

    public static void Apply(AppSettings s)
    {
        ApplyTheme(s.Theme);
        ApplyAccent(s.AccentColor);
        ApplyFont(s);
    }

    const string Pack = "pack://application:,,,/FlashTrans;component/Themes/";

    public static void ApplyTheme(AppTheme theme)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        // 绝对 pack URI：换宿主程序（例如自测程序）也能定位到资源
        var next = new ResourceDictionary { Source = new Uri(Pack + Name(theme), UriKind.Absolute) };

        // 约定：[0] 是配色，[1] 是控件样式。缺了就补上，保证任何宿主里都有完整样式。
        if (dicts.Count > 0) dicts[0] = next;
        else dicts.Add(next);
        if (dicts.Count < 2)
            dicts.Add(new ResourceDictionary { Source = new Uri(Pack + "Controls.xaml", UriKind.Absolute) });

        _current = theme;
    }

    static string Name(AppTheme theme) => theme == AppTheme.Light ? "Light.xaml" : "Dark.xaml";

    public static void ApplyAccent(string hex)
    {
        var color = Parse(hex, Color.FromRgb(0x4C, 0x8D, 0xFF));
        var res = Application.Current.Resources;
        res["Accent"] = new SolidColorBrush(color);
        res["AccentDim"] = new SolidColorBrush(Shade(color, 0.82));
        res["AccentSoft"] = new SolidColorBrush(Color.FromArgb(
            _current == AppTheme.Light ? (byte)0x2A : (byte)0x38, color.R, color.G, color.B));
    }

    static void ApplyFont(AppSettings s)
    {
        var res = Application.Current.Resources;
        res["BaseFontSize"] = s.FontSize;
    }

    public static Color Parse(string hex, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            var o = ColorConverter.ConvertFromString(hex.Trim());
            return o is Color c ? c : fallback;
        }
        catch { return fallback; }
    }

    static Color Shade(Color c, double factor) => Color.FromRgb(
        (byte)Math.Clamp(c.R * factor, 0, 255),
        (byte)Math.Clamp(c.G * factor, 0, 255),
        (byte)Math.Clamp(c.B * factor, 0, 255));

    public static readonly string[] AccentPresets =
        ["#4C8DFF", "#10A37F", "#8B5CF6", "#EC4899", "#F59E0B", "#EF4444", "#06B6D4", "#64748B"];
}
