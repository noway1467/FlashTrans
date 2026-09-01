using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.Views;

/// <summary>
/// 录制中的小浮条：一个闪的红点、录了多久、多少帧，以及怎么停。
///
/// 跟 LongShotHud 一样摆在选区外面——摆里面会被录进去，
/// 而且这条东西上的秒数每帧都在变，录出来满屏都是它在跳。
/// </summary>
public sealed class RecordHud : Window
{
    /// <summary>
    /// 暂停/继续的热键。走 RegisterHotKey 而不是轮询按键状态：注册过的热键会被系统
    /// 吃掉，不会漏进正在录的那个程序里——轮询的话按一次暂停，被录的编辑器里也多了个 p。
    /// </summary>
    public const string PauseHotkey = "Ctrl+Alt+P";

    const int PauseHotkeyId = 0xA1F0;

    readonly TextBlock _text;
    readonly TextBlock _hint;
    readonly TextBlock _pauseLabel;
    readonly Ellipse _dot;
    readonly RECT _region;
    readonly int _maxSeconds;
    int _ticks;
    /// <summary>Esc 已经松开过一次了，从现在起按下才算「停」。见轮询那段。</summary>
    bool _escArmed;
    /// <summary>热键没注册上时的兜底轮询用：上一拍那个组合键是不是按着的。</summary>
    bool _pauseChordDown;
    bool _hotkeyOk;
    bool _encoding;
    int _lastFrames;
    TimeSpan _lastElapsed;

    /// <summary>用户按过 Esc 或者点了浮条。录制那边每帧问一次。</summary>
    public bool Stopped { get; private set; }

    /// <summary>正暂停着。录制那边每拍问一次，为 true 就不抓帧、也不走时钟。</summary>
    public bool Paused { get; private set; }

    public RecordHud(RECT region, int maxSeconds)
    {
        _region = region;
        _maxSeconds = maxSeconds;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        // 不能抢焦点：录的是别人的窗口，一抢它就失去激活态，标题栏变灰、
        // 光标不闪了，录出来的画面跟用户平时看到的不一样。
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        _text = new TextBlock
        {
            Text = $"录制中… 0.0s / {maxSeconds}s",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEC)),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
        };

        _pauseLabel = new TextBlock
        {
            Text = "暂停",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEC)),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
        };

        // 自己拿 Border 拼一个按钮：Button 的默认模板在 AllowsTransparency 的窗口上
        // 会带一层灰底，而且点它要抢焦点。
        var pause = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 2, 8, 3),
            Margin = new Thickness(10, 0, 0, 0),
            Cursor = Cursors.Hand,
            Child = _pauseLabel,
            ToolTip = $"暂停 / 继续（{PauseHotkey}）",
        };
        pause.MouseLeftButtonUp += (_, e) =>
        {
            // 不让它冒到外面那层 Border 上——那层是「点一下就停」。
            e.Handled = true;
            TogglePause();
        };

        _hint = new TextBlock
        {
            Text = $"{PauseHotkey} 暂停 · Esc 停止并保存",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot);
        row.Children.Add(_text);
        row.Children.Add(pause);
        row.Children.Add(_hint);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x20, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 9),
            Cursor = Cursors.Hand,
            Child = row,
            ToolTip = "点一下停止并保存",
        };
        ((Border)Content).MouseLeftButtonUp += (_, _) => Stopped = true;

        // 自己不接键盘（没焦点），只能盯着 Esc 的实时状态。顺便让红点闪起来。
        var poll = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        poll.Tick += (_, _) =>
        {
            WatchEsc((Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) != 0);

            // 热键被别的程序占了才走这条：轮询组合键，只在「刚按下」那一拍翻转，
            // 不然按住 200ms 就来回切好几次。代价是这个 p 会漏进被录的程序里。
            if (!_hotkeyOk) WatchPauseChord(ChordDown());

            // 每 8 拍（约 500ms）翻一次，让人一眼看出还在录。
            // 暂停时不闪：停着的红点 + 「已暂停」才是「真的停住了」的样子。
            if (++_ticks % 8 == 0 && !Paused && !_encoding)
                _dot.Opacity = _dot.Opacity > 0.5 ? 0.25 : 1.0;
        };
        poll.Start();
        Closed += (_, _) => poll.Stop();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            // NOACTIVATE：点它也不抢焦点。TOOLWINDOW：不进任务栏，也不被当成可截的窗口。
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
                ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);

            // 热键注册在这个浮条自己的 HWND 上，随窗口一起消失，不用管全局那份。
            var spec = HotkeySpec.Parse(PauseHotkey);
            if (!spec.IsEmpty)
                _hotkeyOk = Win32.RegisterHotKey(
                    hwnd, PauseHotkeyId, spec.Win32Modifiers, spec.VirtualKey);
            if (!_hotkeyOk)
            {
                Log.Warn($"录制暂停热键 {PauseHotkey} 注册失败，改用轮询");
                _hint.Text = "Esc 停止并保存";   // 热键没拿到就别在界面上承诺它
            }

            System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

            // 对截屏隐身：浮条要显示录制进度，但绝不能被录进片子里。
            // 选区占满屏时 Place 挪不开，只有这个能兜住。
            ScreenHelper.ExcludeFromCapture(this);
        };

        Closed += (_, _) =>
        {
            if (!_hotkeyOk) return;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) Win32.UnregisterHotKey(hwnd, PauseHotkeyId);
        };

        Loaded += (_, _) => Place();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == PauseHotkeyId)
        {
            handled = true;
            TogglePause();
        }
        return IntPtr.Zero;
    }

    /// <summary>暂停键那个组合现在按着没有。</summary>
    static bool ChordDown()
        => (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0
        && (Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) != 0
        && (Win32.GetAsyncKeyState(0x50 /* VK_P */) & 0x8000) != 0;

    /// <summary>只在「上一拍没按、这一拍按下」时翻转。拎出来是为了能测。</summary>
    internal void WatchPauseChord(bool down)
    {
        if (down && !_pauseChordDown) TogglePause();
        _pauseChordDown = down;
    }

    /// <summary>暂停 / 继续。编码阶段按了不算——那时候已经没帧可录了。</summary>
    internal void TogglePause()
    {
        if (_encoding || Stopped) return;
        Paused = !Paused;
        _dot.Opacity = 1.0;
        _dot.Fill = new SolidColorBrush(Paused
            ? Color.FromRgb(0xFF, 0xB0, 0x2E)    // 暂停：琥珀，一眼看出不是在录
            : Color.FromRgb(0xFF, 0x3B, 0x30));
        _pauseLabel.Text = Paused ? "继续" : "暂停";
        Render();
        // 「录制中…」和「已暂停」宽度不一样，SizeToContent 会把浮条往右撑，重新居中一下。
        // Report 那边不这么做：秒数每帧都在变，每帧重摆会看着抖。
        if (IsLoaded) Place();
    }

    /// <summary>
    /// Esc 的判定。要等它松开过一次才开始算：用户拿 Esc 关掉别的东西之后马上点
    /// 「录制」，或者手还搭在 Esc 上，这时候键是按下状态——直接采信的话，录制刚
    /// 开始就被判成「用户要停」，一帧都没录到，而屏幕上只剩一句「录制没成功」，
    /// 根本看不出是这个原因。
    ///
    /// 单独拎出来是为了能测：真去注入一次 Esc 的话，按键会落到当前有焦点的那个
    /// 窗口上（这个浮条自己是 NOACTIVATE，从不抢焦点），自测不能那么干。
    /// </summary>
    internal void WatchEsc(bool escDown)
    {
        if (!escDown) _escArmed = true;
        else if (_escArmed) Stopped = true;
    }

    public void Report(int frames, TimeSpan elapsed)
    {
        // 记下来：暂停期间没有新的 Report 进来，但文字要从「录制中」改成「已暂停」，
        // 那时候得靠这两个值把秒数和帧数原样留在屏幕上。
        _lastFrames = frames;
        _lastElapsed = elapsed;
        Render();
    }

    void Render()
    {
        if (_encoding) return;
        var head = Paused ? "已暂停" : "录制中…";
        _text.Text = $"{head} {_lastElapsed.TotalSeconds:0.0}s / {_maxSeconds}s（{_lastFrames} 帧）";
    }

    /// <summary>编码可能要几秒，把浮条留着改个说法，别让屏幕上什么反馈都没有。</summary>
    public void ReportEncoding(int frames)
    {
        _encoding = true;
        _dot.Opacity = 1.0;
        _dot.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
        _text.Text = $"正在编码 {frames} 帧…";
        _pauseLabel.Text = "暂停";
        _hint.Text = "";
    }

    /// <summary>
    /// 摆在选区正下方居中；下面不够就摆上面；上下都挤不出地方（选区占满屏）
    /// 就贴到工作区底部——那时候难免会被录进去一点，但总比看不见进度好。
    /// </summary>
    void Place()
    {
        UpdateLayout();
        var w = ActualWidth > 0 ? ActualWidth : 260;
        var h = ActualHeight > 0 ? ActualHeight : 36;

        // 选区是物理像素，Left/Top 是 DIP，得换一下
        var tl = ScreenHelper.ToDip(new POINT { X = _region.Left, Y = _region.Top }, this);
        var br = ScreenHelper.ToDip(new POINT { X = _region.Right, Y = _region.Bottom }, this);
        var work = ScreenHelper.WorkAreaAt(new POINT { X = _region.Left, Y = _region.Bottom }, this);

        Left = Math.Clamp((tl.X + br.X) / 2 - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        var top = br.Y + 8;
        if (top + h > work.Bottom) top = tl.Y - h - 8;
        if (top < work.Top) top = work.Bottom - h - 8;
        Top = top;
    }
}
