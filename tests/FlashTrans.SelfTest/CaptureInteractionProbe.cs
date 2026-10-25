using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

static class CaptureInteractionProbe
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunAll(Action<string, Action> step)
    {
        step("取色：多 DPI 原像素、边界及 HEX/RGB 格式", SamplePixels);
        step("文字：真实输入框边缘直接拖动，提交后无需退出文字工具", TextDrag);
        step("取色：真实按钮、鼠标复制、Shift 格式与右键返回", ColorCopy);
    }

    static CapturedImage Pattern(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)x;
                pixels[offset + 1] = (byte)y;
                pixels[offset + 2] = 0xA5;
                pixels[offset + 3] = 255;
            }
        return new CapturedImage(width, height, pixels);
    }

    static void SamplePixels()
    {
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            var layer = (CaptureSelectionLayer)CaptureOverlay.LayerForShot(Pattern(400, 300),
                new Size(400 / scale, 300 / scale), new Rect(10, 10, 80, 80));
            layer.AddAnnotation(new RedactionAnnotation { Bounds = new Rect(10, 10, 80, 80) });
            foreach (var (x, y) in new[] { (0, 0), (21, 41), (399, 299) })
            {
                Check(layer.TrySampleColor(new Point((x + .1) / scale, (y + .1) / scale), out var color, out var pixel), "取色返回空");
                Check(pixel == new Point(x, y) && color == Color.FromRgb(0xA5, (byte)y, (byte)x), "DPI 坐标或 BGRA 通道错误");
            }
            Check(!layer.TrySampleColor(new Point(-1, 0), out _, out _)
                && !layer.TrySampleColor(new Point(layer.ActualWidth, 0), out _, out _), "越界取色没有拒绝");
        }
        var sample = Color.FromRgb(1, 15, 255);
        Check(CaptureOverlay.FormatSampledColor(sample, false) == "#010FFF", "HEX 大写及补零错误");
        Check(CaptureOverlay.FormatSampledColor(sample, true) == "rgb(1, 15, 255)", "RGB 格式错误");
        Check(CaptureOverlay.IsTextFrameEdge(new Point(2, 20), new Size(100, 40)), "编辑框边缘不可拖");
        Check(!CaptureOverlay.IsTextFrameEdge(new Point(50, 20), new Size(100, 40)), "输入区被当作移动区");
    }

    static void WithWindow(Action<CaptureOverlay, CaptureSelectionLayer> run)
    {
        var settings = SettingsService.Instance.Current;
        var auto = settings.CaptureAutoRedact;
        var size = settings.CaptureFontSize;
        Win32.GetCursorPos(out var cursor);
        CaptureOverlay? window = null;
        try
        {
            settings.CaptureAutoRedact = false;
            settings.CaptureFontSize = 24;
            window = new CaptureOverlay(Pattern(900, 500), default)
            {
                Width = 900, Height = 500, Left = 20, Top = 20,
            };
            window.Deactivated += (_, _) =>
            {
                var foreground = Win32.GetForegroundWindow();
                Win32.GetWindowThreadProcessId(foreground, out var pid);
                Console.WriteLine($"       测试蒙层失去前台：PID={pid}，当前={Environment.ProcessId}");
            };
            window.Show(); window.UpdateLayout();
            window.Activate(); window.Focus();
            var layer = Field<CaptureSelectionLayer>(window, "_layer");
            layer.PresetSelection(new Rect(30, 30, 700, 270));
            Call(window, "OnSelectionChanged");
            Pump();
            Check(window.IsActive, "测试窗口未获得前台，不能开始真实鼠标验证");
            run(window, layer);
        }
        finally
        {
            window?.Close();
            settings.CaptureAutoRedact = auto;
            settings.CaptureFontSize = size;
            Win32.SetCursorPos(cursor.X, cursor.Y);
            Pump();
        }
    }

    static void TextDrag() => WithWindow((window, layer) =>
    {
        Call(window, "ToggleTool", CaptureTool.Text);
        Click(layer, new Point(90, 75));
        var box = Field<TextBox>(window, "_textBox");
        box.Text = "移动文字 Drag me";
        box.CaretIndex = 3;
        Pump();
        var frame = Field<Border>(window, "_textFrame");
        var start = new Point(Canvas.GetLeft(frame), Canvas.GetTop(frame));
        Drag(frame, new Point(2, frame.ActualHeight / 2), new Vector(70, 40));
        Check(Math.Abs(Canvas.GetLeft(frame) - start.X - 70) < 1
            && Math.Abs(Canvas.GetTop(frame) - start.Y - 40) < 1,
            $"编辑框位移错误：起点={start}，终点={Canvas.GetLeft(frame)},{Canvas.GetTop(frame)}，活动={window.IsActive}，可见={window.IsVisible}");
        Check(ReferenceEquals(box, Field<TextBox>(window, "_textBox")) && box.IsKeyboardFocusWithin
            && box.Text == "移动文字 Drag me" && box.CaretIndex == 3,
            $"拖动丢失编辑状态：焦点={box.IsKeyboardFocusWithin}，光标={box.CaretIndex}，活动={window.IsActive}，可见={window.IsVisible}");
        Check(layer.AnnotationCount == 0 && layer.Tool == CaptureTool.Text, "拖动不应提交或切换工具");
        Preview(window, "text-drag.png");
        var moved = new Point(Canvas.GetLeft(frame), Canvas.GetTop(frame));
        Drag(box, new Point(10, box.ActualHeight / 2), new Vector(25, 0));
        Check(new Point(Canvas.GetLeft(frame), Canvas.GetTop(frame)) == moved, "框内选字错误地移动了文字框");

        Call(window, "CommitTextInput");
        var text = layer.Items.OfType<TextAnnotation>().Single();
        var at = text.At;
        Check((at - (moved + new Vector(6, 6))).Length < .1, "提交文字没有使用拖动后的坐标");
        var hit = new Point(text.Extent.Left + text.Extent.Width / 2, text.Extent.Top + text.Extent.Height / 2);
        Drag(layer, hit, new Vector(60, 30));
        Check((text.At - at - new Vector(60, 30)).Length < 1, "文字工具仍选中时不能拖动已有标注");
        Check(layer.AnnotationCount == 1 && Field<TextBox?>(window, "_textBox") is null, "拖动已有文字误建了输入框");
        Check(Mouse.Captured is null, "拖动后没有释放鼠标捕获");
    });

    static void ColorCopy()
    {
        using var clipboard = ClipboardSnapshot.Capture();
        uint sequence = 0;
        try
        {
            WithWindow((window, layer) =>
            {
                var row = Field<Panel>(window, "_actionRow");
                var button = row.Children.OfType<Button>().Single(b => b.ToolTip?.ToString()?.StartsWith("原图取色") == true);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(layer.Tool == CaptureTool.ColorPicker, "按钮没有开启取色模式");
                var at = new Point(100, 100);
                layer.TrySampleColor(at, out var expected, out _);
                Click(layer, at);
                sequence = Win32.GetClipboardSequenceNumber();
                Check(Clipboard.GetText() == CaptureOverlay.FormatSampledColor(expected, false), "真实单击没有复制原像素 HEX");
                Key(0x10, true);
                try
                {
                    Check((Keyboard.Modifiers & ModifierKeys.Shift) != 0, "测试 Shift 按下未送达活动窗口");
                    Click(layer, at);
                }
                finally { Key(0x10, false); }
                sequence = Win32.GetClipboardSequenceNumber();
                Check(Clipboard.GetText() == CaptureOverlay.FormatSampledColor(expected, true), "Shift+单击没有复制 RGB");
                Preview(window, "color-picker.png");
                var selection = layer.Selection;
                Click(layer, at, right: true);
                Check(layer.Tool == CaptureTool.None && layer.Selection == selection && layer.AnnotationCount == 0,
                    "右键退出取色改变了选区或标注");
                Check(Field<Border>(window, "_colorPreview").Visibility == Visibility.Collapsed, "退出取色后提示未隐藏");
            });
        }
        finally { if (sequence != 0) clipboard.Restore(sequence); }
    }

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    static void Preview(CaptureOverlay window, string name)
    {
        if (Environment.GetEnvironmentVariable("FLASHTRANS_CAPTURE_PREVIEW") is not { Length: > 0 } dir) return;
        var canvas = Field<Canvas>(window, "_canvas");
        canvas.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)canvas.ActualWidth, (int)canvas.ActualHeight,
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        new CapturedImage(bitmap.PixelWidth, bitmap.PixelHeight, pixels).SavePng(System.IO.Path.Combine(dir, name));
    }

    static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(70) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    static void Pointer(UIElement element, Point at)
    {
        var screen = element.PointToScreen(at);
        Check(Win32.SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)), "无法设置测试鼠标位置");
        Pump();
    }

    static void MouseButton(uint flags)
    {
        var input = new INPUT { type = Win32.INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } } };
        Check(Win32.SendInput(1, [input], Marshal.SizeOf<INPUT>()) == 1, "鼠标测试输入被系统拒绝");
        Pump();
    }

    static void Key(ushort key, bool down)
    {
        var input = new INPUT { type = Win32.INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = key, dwFlags = down ? 0 : Win32.KEYEVENTF_KEYUP } } };
        Check(Win32.SendInput(1, [input], Marshal.SizeOf<INPUT>()) == 1, "键盘测试输入被系统拒绝");
        Pump();
    }

    static void Click(UIElement element, Point at, bool right = false)
    {
        Pointer(element, at);
        try { MouseButton(right ? 0x0008u : 0x0002u); }
        finally { MouseButton(right ? 0x0010u : 0x0004u); }
    }

    static void Drag(UIElement element, Point at, Vector delta)
    {
        Pointer(element, at);
        var target = element.PointToScreen(at + delta);
        try
        {
            MouseButton(0x0002);
            Win32.SetCursorPos((int)Math.Round(target.X), (int)Math.Round(target.Y));
            Pump();
        }
        finally { MouseButton(0x0004); }
    }
}
