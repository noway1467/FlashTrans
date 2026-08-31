using System.Text;
using System.Windows.Input;

namespace FlashTrans.Interop;

/// <summary>快捷键描述，形如 "Ctrl+Alt+Q"。</summary>
public sealed record HotkeySpec(ModifierKeys Modifiers, Key Key)
{
    public static readonly HotkeySpec None = new(ModifierKeys.None, Key.None);

    public bool IsEmpty => Key == Key.None;

    public uint Win32Modifiers
    {
        get
        {
            uint m = Win32.MOD_NOREPEAT;
            if (Modifiers.HasFlag(ModifierKeys.Control)) m |= Win32.MOD_CONTROL;
            if (Modifiers.HasFlag(ModifierKeys.Alt)) m |= Win32.MOD_ALT;
            if (Modifiers.HasFlag(ModifierKeys.Shift)) m |= Win32.MOD_SHIFT;
            if (Modifiers.HasFlag(ModifierKeys.Windows)) m |= Win32.MOD_WIN;
            return m;
        }
    }

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public static HotkeySpec Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        var mods = ModifierKeys.None;
        var key = Key.None;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win" or "windows" or "meta": mods |= ModifierKeys.Windows; break;
                default:
                    if (Enum.TryParse<Key>(Normalize(raw), ignoreCase: true, out var k)) key = k;
                    break;
            }
        }
        return key == Key.None ? None : new HotkeySpec(mods, key);
    }

    static string Normalize(string s) => s.Length == 1 && char.IsDigit(s[0]) ? "D" + s : s;

    public override string ToString()
    {
        if (IsEmpty) return "";
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        sb.Append(Display(Key));
        return sb.ToString();
    }

    static string Display(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        Key.Oem3 => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.Oem6 => "]",
        Key.Oem5 => "\\",
        Key.Space => "Space",
        _ => k.ToString()
    };

    /// <summary>从按键事件生成（用于设置页录制）。</summary>
    public static HotkeySpec? FromKeyEvent(Key key, ModifierKeys mods)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None or Key.ImeProcessed) return null;
        if (mods == ModifierKeys.None) return null;   // 必须带修饰键，避免抢占普通输入
        return new HotkeySpec(mods, key);
    }
}
