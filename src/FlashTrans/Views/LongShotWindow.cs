using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlashTrans.Core;
using FlashTrans.Interop;

namespace FlashTrans.Views;

/// <summary>
/// 长图截完之后的预览。长图动不动几千像素高，直接塞剪贴板或者存盘用户也不知道
/// 拼成什么样了，先让他看一眼再挑动作。
/// </summary>
public sealed class LongShotWindow : Window
{
    /// <summary>用户挑了哪个动作。宿主接着走和普通截图一样的处理。</summary>
    public event Action<CaptureAction>? Action;

    public LongShotWindow(CapturedImage image, string note)
    {
        Title = $"长截图 {image.Width}×{image.Height}";
        Width = 520;
        Height = 660;
        MinWidth = 360;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SetResourceReference(BackgroundProperty, "Bg");

        var pic = new Image
        {
            Source = image.ToBitmap(),
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(pic, BitmapScalingMode.HighQuality);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 12, 12, 0),
            Content = pic,
        };
        scroll.SetResourceReference(StyleProperty, "PlainScrollViewer");

        var head = UiKit.Text($"{image.Width} × {image.Height} 像素" + (note.Length > 0 ? "　" + note : ""),
                              12.5, "TextDim");
        head.Margin = new Thickness(14, 10, 14, 0);

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(14, 10, 14, 12),
        };
        bar.Children.Add(Btn("识别文字", CaptureAction.Ocr, "GhostBtn"));
        bar.Children.Add(Btn("识别并翻译", CaptureAction.OcrTranslate, "GhostBtn"));
        bar.Children.Add(Btn("复制", CaptureAction.Copy, "OutlineBtn"));
        bar.Children.Add(Btn("保存", CaptureAction.Save, "PrimaryBtn"));

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        UiKit.SetGrid(head);
        UiKit.SetGrid(scroll, row: 1);
        UiKit.SetGrid(bar, row: 2);
        grid.Children.Add(head);
        grid.Children.Add(scroll);
        grid.Children.Add(bar);
        Content = grid;

        PreviewKeyDown += (_, e) =>
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.Escape: e.Handled = true; Fire(CaptureAction.None); break;
                case Key.C when ctrl: e.Handled = true; Fire(CaptureAction.Copy); break;
                case Key.S when ctrl: e.Handled = true; Fire(CaptureAction.Save); break;
                case Key.D when ctrl:
                    e.Handled = true;
                    Fire(shift ? CaptureAction.OcrTranslate : CaptureAction.Ocr);
                    break;
            }
        };
    }

    Button Btn(string label, CaptureAction action, string style)
    {
        var b = new Button
        {
            Content = label,
            MinWidth = 84,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
        };
        b.SetResourceReference(StyleProperty, style);
        b.Click += (_, _) => Fire(action);
        return b;
    }

    void Fire(CaptureAction action)
    {
        // 只认第一次。Close 到窗口真的消失之间还会处理消息，这中间再按一下
        // Ctrl+S 就会走第二遍：动作干两回（弹两个「另存为」），
        // 而且第二次的 Close 撞在正在关闭的窗口上会抛异常。
        if (_fired) return;
        _fired = true;

        // 先收起自己再干活：存图会弹「另存为」，识别会开主窗口，
        // 这个预览留在后面只是挡视线。
        Close();
        Action?.Invoke(action);
    }

    bool _fired;
}
