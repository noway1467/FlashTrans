using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using FlashTrans.Interop;

namespace FlashTrans.Views;

/// <summary>对话框语气，决定图标和主按钮配色。</summary>
public enum DialogTone { Question, Warning, Danger, Info }

/// <summary>
/// 跟着主题走的模态对话框，替掉系统 MessageBox。
/// 圆角、投影、遮住父窗口的蒙层，按钮用应用自己的样式。
/// </summary>
public sealed class AppDialog : Window
{
    bool _accepted;

    AppDialog(Window? owner, string title, string message, string? detail,
              string okText, string? cancelText, DialogTone tone, Geometry? icon)
    {
        Title = title;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        // 圆角要靠 AllowsTransparency 才透得出去，窗口本身必须是透明的，
        // 背景色画在里层 Border 上。
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontSize = 13;
        SetResourceReference(ForegroundProperty, "Text");

        if (owner is { IsLoaded: true })
        {
            Owner = owner;
            Topmost = owner.Topmost;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Content = BuildShell(title, message, detail, okText, cancelText, tone, icon);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            // 回车只在非危险语气下直接确认。危险语气的焦点是「取消」，
            // 这里要是拦下回车就等于把焦点白放了，交给按钮自己处理。
            else if (e.Key == Key.Enter && tone != DialogTone.Danger) { e.Handled = true; Accept(); }
        };
    }

    void Accept()
    {
        _accepted = true;
        Close();
    }

    UIElement BuildShell(string title, string message, string? detail,
                         string okText, string? cancelText, DialogTone tone, Geometry? icon)
    {
        var body = new StackPanel();

        // 标题行：色块图标 + 标题
        var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = ToneBadge(tone, icon);
        UiKit.SetGrid(badge, col: 0);
        head.Children.Add(badge);

        var titleText = UiKit.Text(title, 14.5, "Text", FontWeights.SemiBold, wrap: true);
        titleText.Margin = new Thickness(10, 0, 0, 0);
        UiKit.SetGrid(titleText, col: 1);
        head.Children.Add(titleText);
        body.Children.Add(head);

        var msg = UiKit.Text(message, 12.5, "TextDim", wrap: true);
        msg.LineHeight = 19;
        msg.Margin = new Thickness(0, 0, 0, detail is null ? 16 : 8);
        body.Children.Add(msg);

        if (detail is not null)
        {
            var box = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 7, 9, 8),
                Margin = new Thickness(0, 0, 0, 16),
                BorderThickness = new Thickness(1),
            };
            box.SetResourceReference(Border.BackgroundProperty, "BgAlt");
            box.SetResourceReference(Border.BorderBrushProperty, "Border");
            // 用 TextDim 而不是 TextFaint：这行是「会丢什么」的正文，
            // TextFaint 在浅色主题下只有 2.8:1，达不到 WCAG AA 的 4.5:1。
            var dt = UiKit.Text(detail, 11.5, "TextDim", wrap: true);
            dt.LineHeight = 17;
            box.Child = dt;
            body.Children.Add(box);
        }

        // 按钮行：取消在左，主操作在右
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        Button? cancel = null;
        if (cancelText is not null)
        {
            cancel = new Button
            {
                Content = cancelText,
                FontSize = 12.5,
                Padding = new Thickness(16, 5, 16, 5),
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 78,
            };
            cancel.SetResourceReference(StyleProperty, "OutlineBtn");
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);
        }

        var ok = new Button
        {
            Content = okText,
            FontSize = 12.5,
            Padding = new Thickness(16, 5, 16, 5),
            MinWidth = 78,
        };
        ok.SetResourceReference(StyleProperty, tone == DialogTone.Danger ? "DangerBtn" : "PrimaryBtn");
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        body.Children.Add(buttons);

        // 删除、重置这类不可逆的操作，焦点默认落在「取消」上，
        // 免得顺手一个回车就执行了。
        var focusTarget = tone == DialogTone.Danger && cancel is not null ? cancel : ok;
        Loaded += (_, _) => focusTarget.Focus();

        var panel = new Border
        {
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16, 18, 15),
            MinWidth = 320,
            MaxWidth = 420,
            Child = body,
            // 投影画在 Margin 里，窗口本身是透明的，所以留出空间就不会被裁掉。
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.42, Color = Colors.Black },
            Margin = new Thickness(14),
        };
        panel.SetResourceReference(Border.BackgroundProperty, "Bg");
        panel.SetResourceReference(Border.BorderBrushProperty, "Border");

        // 空白处也能拖动
        panel.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { /* 忽略 */ } };
        return panel;
    }

    static Border ToneBadge(DialogTone tone, Geometry? custom)
    {
        // 语气只决定颜色。图标可以单独给：删除配垃圾桶，重置配警告三角，
        // 两个都是 Danger，但画同一个垃圾桶就不对了。
        var brush = tone switch
        {
            DialogTone.Danger => "Danger",
            DialogTone.Warning => "Warn",
            _ => "Accent",
        };
        var geom = custom ?? tone switch
        {
            DialogTone.Danger or DialogTone.Warning => UiKit.IconWarn,
            _ => UiKit.IconInfo,
        };

        var icon = UiKit.Icon(geom, 15, thickness: 1.5);
        icon.SetResourceReference(Shape.StrokeProperty, brush);

        var badge = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Top,
            Child = icon,
        };
        badge.SetResourceReference(Border.BackgroundProperty, "BgAlt");
        badge.SetResourceReference(Border.BorderBrushProperty, "Border");
        badge.BorderThickness = new Thickness(1);
        return badge;
    }

    // ------------------------------------------------------------- 入口

    /// <summary>确认框。返回 true 表示用户点了主按钮。</summary>
    public static bool Confirm(Window? owner, string title, string message,
                               string okText = "确定", string cancelText = "取消",
                               DialogTone tone = DialogTone.Question, string? detail = null,
                               Geometry? icon = null)
        => Run(owner, title, message, detail, okText, cancelText, tone, icon);

    /// <summary>只有一个「知道了」的提示框。</summary>
    public static void Info(Window? owner, string title, string message,
                            string okText = "知道了", DialogTone tone = DialogTone.Info,
                            string? detail = null, Geometry? icon = null)
        => Run(owner, title, message, detail, okText, null, tone, icon);

    static bool Run(Window? owner, string title, string message, string? detail,
                    string okText, string? cancelText, DialogTone tone, Geometry? icon)
    {
        var dlg = new AppDialog(owner, title, message, detail, okText, cancelText, tone, icon);
        var scrim = Scrim.Show(owner);
        try { dlg.ShowDialog(); }
        finally { scrim?.Close(); }
        return dlg._accepted;
    }

    /// <summary>压在父窗口上的半透明蒙层，让对话框看起来是「浮」在上面的。</summary>
    sealed class Scrim : Window
    {
        public static Scrim? Show(Window? owner)
        {
            if (owner is not { IsLoaded: true, IsVisible: true }) return null;
            try
            {
                var s = new Scrim(owner) { Owner = owner };
                if (!s.Cover(owner)) return null;
                s.Show();
                return s;
            }
            catch { return null; }   // 蒙层只是装饰，失败就不要了
        }

        Scrim(Window owner)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(0x3C, 0, 0, 0));
            ShowInTaskbar = false;
            ShowActivated = false;      // 不抢焦点，否则会打断模态窗口的激活
            Focusable = false;
            // 设置窗是 Topmost 的，蒙层不跟上就会被压到它下面，等于没盖。
            // 对话框随后被 ShowDialog 激活，仍然在蒙层之上。
            Topmost = owner.Topmost;
        }

        /// <summary>盖住父窗口的客户区。最大化时 Left/Top 不可靠，所以走屏幕坐标。</summary>
        bool Cover(Window owner)
        {
            if (owner.ActualWidth <= 0 || owner.ActualHeight <= 0) return false;

            var origin = owner.PointToScreen(new Point(0, 0));
            var dip = ScreenHelper.ToDip(
                new POINT { X = (int)Math.Round(origin.X), Y = (int)Math.Round(origin.Y) }, owner);

            Left = dip.X;
            Top = dip.Y;
            Width = owner.ActualWidth;
            Height = owner.ActualHeight;
            return true;
        }
    }
}
