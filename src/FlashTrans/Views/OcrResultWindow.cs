using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlashTrans.Views;

/// <summary>
/// 识别结果。识别难免有错字（小字号、花底色、竖排都会掉准确率），
/// 所以不直接把字扔进剪贴板，而是摆在一个能改的框里，
/// 改完再挑复制还是翻译。
/// </summary>
public sealed class OcrResultWindow : Window
{
    /// <summary>用户改完之后要复制。参数是框里当时的内容。</summary>
    public event Action<string>? Copy;
    /// <summary>用户改完之后要翻译。</summary>
    public event Action<string>? Translate;

    readonly TextBox _box;

    public OcrResultWindow(string text)
    {
        Title = "识别结果";
        Width = 520;
        Height = 360;
        MinWidth = 360;
        MinHeight = 220;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SetResourceReference(BackgroundProperty, "Bg");

        _box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // 全局那个 TextBox 样式把内容竖向居中——单行输入框该那样，
            // 这个框有好几行，居中的话字浮在框中间，上面下面各空一块。
            VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 14,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(14, 10, 14, 0),
        };

        var head = UiKit.Text(Count(text), 12.5, "TextDim");
        head.Margin = new Thickness(15, 10, 14, 0);
        // 改了字数要跟着变，不然那行数字跟框里的内容对不上
        _box.TextChanged += (_, _) => head.Text = Count(_box.Text);

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(14, 10, 14, 12),
        };
        bar.Children.Add(Btn("关闭 (Esc)", "GhostBtn", Close));
        bar.Children.Add(Btn("翻译 (Ctrl+Enter)", "OutlineBtn", FireTranslate));
        bar.Children.Add(Btn("复制 (Ctrl+C)", "PrimaryBtn", FireCopy));

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        UiKit.SetGrid(head);
        UiKit.SetGrid(_box, row: 1);
        UiKit.SetGrid(bar, row: 2);
        grid.Children.Add(head);
        grid.Children.Add(_box);
        grid.Children.Add(bar);
        Content = grid;

        PreviewKeyDown += OnKey;

        // 一打开就选中全部：多数时候识别得对，直接 Ctrl+C 走人；
        // 要改的话按一下方向键就取消选中了，不挡事。
        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            // 框里没选中任何东西时 Ctrl+C 才当成「复制全部并关窗」。
            // 选了一段的话让文本框自己复制那一段——在能编辑的框里
            // 抢掉 Ctrl+C 会很别扭。
            case Key.C when ctrl && _box.SelectionLength == 0:
                e.Handled = true;
                FireCopy();
                break;
            case Key.Enter when ctrl:
                e.Handled = true;
                FireTranslate();
                break;
        }
    }

    static string Count(string s)
    {
        var n = s.Trim().Length;
        return n == 0 ? "空的" : $"{n} 个字符　·　可以改完再复制或翻译";
    }

    void FireCopy() => Fire(t => Copy?.Invoke(t));

    void FireTranslate() => Fire(t => Translate?.Invoke(t));

    /// <summary>
    /// 收工。跟 LongShotWindow 一样只认第一次：Close 到窗口真的没了之间还在处理消息，
    /// 这中间再按一次就会把动作干两遍，而且第二次 Close 会撞在正在关闭的窗口上抛异常。
    /// </summary>
    void Fire(Action<string> run)
    {
        if (_fired) return;
        var text = _box.Text.Trim();
        if (text.Length == 0) return;   // 全删空了就没什么可干的，留着窗口让用户接着改

        _fired = true;
        Close();
        run(text);
    }

    bool _fired;

    Button Btn(string label, string style, Action onClick)
    {
        var b = new Button
        {
            Content = label,
            MinWidth = 84,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
        };
        b.SetResourceReference(StyleProperty, style);
        b.Click += (_, _) => onClick();
        return b;
    }
}
