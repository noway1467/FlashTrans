using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

static class CaptureHotkeyProbe
{
    public static void RunAll(Action<string, Action> step)
    {
        step("截图热键：真实输入不重复，修饰键精确匹配，改键和释放生效", Lifecycle);
        step("截图热键：原生二级菜单展开时呼出截图，菜单像素被保留", NativeSubmenu);
        step("截图遮罩：目标窗口短暂抢焦点后仍可操作，稳定后切走正常关闭", ActivationBounce);
        step("截图遮罩：首次聚焦时关闭能正常返回，下一次截图仍可使用", EarlyClose);
    }

    static void ActivationBounce()
    {
        var foreground = Win32.GetForegroundWindow();
        var target = new Window { Width = 300, Height = 150, Left = 100, Top = 100,
            ShowInTaskbar = false, Topmost = true, Content = new TextBox { Text = "模拟软件恢复焦点" } };
        var pixels = new byte[640 * 360 * 4];
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var overlay = new CaptureOverlay(new CapturedImage(640, 360, pixels), default)
        { Width = 640, Height = 360, Left = 80, Top = 80 };
        var closed = false;
        overlay.Closed += (_, _) => closed = true;
        try
        {
            target.Show(); target.Activate(); Pump();
            overlay.Show(); overlay.Activate(); overlay.UpdateLayout();
            Check(overlay.IsActive, "测试遮罩未激活，无法复现焦点争抢");
            target.Activate();
            Check(!closed, "目标软件短暂恢复焦点就把截图遮罩关闭了");
            Wait(() => overlay.IsActive || closed, "遮罩未恢复输入焦点");
            Check(!closed && overlay.IsActive, "遮罩恢复前已关闭");
            var until = Environment.TickCount64 + 500;
            while (Environment.TickCount64 < until) { Pump(); Thread.Sleep(10); }
            target.Activate();
            Wait(() => closed, "稳定后切换窗口没有关闭遮罩");
        }
        finally
        {
            if (!closed) overlay.Close();
            target.Close(); Pump();
            if (foreground != IntPtr.Zero) Win32.SetForegroundWindow(foreground);
        }
    }

    static void EarlyClose()
    {
        var intercept = true;
        var closedDuringFocus = false;
        var attached = new HashSet<CaptureOverlay>();
        // 构造期间的布局调度可观察到新窗口；在 Show 首次激活前接上关闭事件。
        // 空选区没有可聚焦的子控件，GotFocus 并不能作为这个场景的触发点。
        DispatcherHookEventHandler attach = (_, _) =>
        {
            if (!intercept) return;
            foreach (var overlay in Application.Current.Windows.OfType<CaptureOverlay>())
                if (attached.Add(overlay)) overlay.Activated += CloseOnActivation;
        };
        void CloseOnActivation(object? sender, EventArgs args)
        {
            if (!intercept || sender is not CaptureOverlay overlay) return;
            intercept = false;
            closedDuringFocus = true;
            overlay.Close();
        }
        Dispatcher.CurrentDispatcher.Hooks.OperationPosted += attach;
        var timedOut = false;
        var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        guard.Tick += (_, _) => { timedOut = true; guard.Stop(); Dispatcher.ExitAllFrames(); };
        try
        {
            guard.Start();
            var outcome = CaptureOverlay.Pick();
            Check(closedDuringFocus && !timedOut && outcome.Action == CaptureAction.None,
                $"提前关闭未返回取消：焦点回调={closedDuringFocus}，超时={timedOut}，动作={outcome.Action}");
            guard.Stop();
            var closeNext = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            closeNext.Tick += (_, _) =>
            {
                foreach (var overlay in Application.Current.Windows.OfType<CaptureOverlay>().ToArray()) overlay.Close();
                closeNext.Stop();
            };
            try
            {
                guard.Start(); closeNext.Start();
                Check(CaptureOverlay.Pick().Action == CaptureAction.None && !timedOut, "第二次截图不能正常结束");
            }
            finally { closeNext.Stop(); }
        }
        finally
        {
            intercept = false; guard.Stop();
            Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= attach;
            foreach (var overlay in attached) overlay.Activated -= CloseOnActivation;
            foreach (var overlay in Application.Current.Windows.OfType<CaptureOverlay>().ToArray()) overlay.Close();
        }
    }

    static void Lifecycle()
    {
        using var message = new MessageWindow("CaptureHotkey.Lifecycle");
        using var manager = new HotkeyManager(message.Handle);
        message.Message += (msg, wp, _) => msg == Win32.WM_HOTKEY && manager.Handle(wp);
        var first = AvailableKey();
        var next = new HotkeySpec(ModifierKeys.Control | ModifierKeys.Alt, first.Key);
        var settings = AppSettings.CreateDefault();
        var count = 0;
        manager.Triggered += action => { if (action == HotkeyAction.CaptureOcr) count++; };
        var target = new Window { Content = new TextBox(), Width = 300, Height = 120, ShowInTaskbar = false, Topmost = true };
        var foreground = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var originalCursor);
        try
        {
            target.Show(); Pump(); FocusTarget(target);
            Check(manager.TryChange(settings, HotkeyAction.CaptureOcr, first.ToString(), out var error), error ?? "注册失败");
            Send(first, repeats: 4);
            Wait(() => count == 1, "按住截图键未触发或重复触发");
            Send(first);
            Wait(() => count == 2, "松开后再次按键未触发");
            Send(new HotkeySpec(ModifierKeys.Shift, first.Key));
            Pump();
            Check(count == 2, "额外按着 Shift 也触发了裸键截图");

            Check(manager.TryChange(settings, HotkeyAction.CaptureOcr, next.ToString(), out error), error ?? "改键失败");
            Send(first); Pump();
            Check(count == 2, "改键后旧键仍触发截图");
            Send(next, repeats: 3);
            Wait(() => count == 3, "带修饰键的截图触发错误");
            settings.HkToggleWindow = first.ToString();
            Check(!manager.TryChange(settings, HotkeyAction.CaptureOcr, first.ToString(), out error), "未阻止配置中的快捷键冲突");
            Send(next);
            Wait(() => count == 4, "冲突后原截图键失效");
            Check(manager.TryChange(settings, HotkeyAction.CaptureOcr, "", out _), "清除截图快捷键失败");
            Send(next); Pump();
            Check(count == 4, "清除后钩子仍触发截图");
            Check(Win32.RegisterHotKey(message.Handle, 0x7110, next.Win32Modifiers, next.VirtualKey), "清除后系统热键未释放");
            Win32.UnregisterHotKey(message.Handle, 0x7110);
        }
        finally
        {
            target.Close();
            Win32.SetCursorPos(originalCursor.X, originalCursor.Y);
            if (foreground != IntPtr.Zero) Win32.SetForegroundWindow(foreground);
        }
    }

    static void NativeSubmenu()
    {
        using var message = new MessageWindow("CaptureHotkey.Menu");
        using var manager = new HotkeyManager(message.Handle);
        var fromHook = false;
        message.Message += (msg, wp, lp) =>
        {
            if (msg != Win32.WM_HOTKEY) return false;
            fromHook |= lp == IntPtr.Zero;
            return manager.Handle(wp);
        };
        var spec = AvailableKey();
        Check(manager.TryChange(AppSettings.CreateDefault(), HotkeyAction.CaptureOcr, spec.ToString(), out var error),
            error ?? "菜单测试注册失败");
        var window = new Window { Title = "截图菜单回归", Width = 440, Height = 220, Left = 100, Top = 100,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, Topmost = true,
            Background = System.Windows.Media.Brushes.White };
        var foreground = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var originalCursor);
        var menu = CreatePopupMenu();
        var submenu = CreatePopupMenu();
        Check(menu != IntPtr.Zero && submenu != IntPtr.Zero, "无法创建原生菜单");
        AppendMenu(submenu, 0, new UIntPtr(1), "二级菜单截图测试 / submenu");
        AppendMenu(menu, 0x10, (UIntPtr)submenu, "流量监控（测试菜单）");
        var stage = 0;
        var count = 0;
        var complete = false;
        Exception? failure = null;
        RECT rect = default;
        CapturedImage? expected = null;
        Task? captureTask = null;
        var host = new AppHost();
        manager.Triggered += action =>
        {
            if (action != HotkeyAction.CaptureOcr) return;
            count++;
            captureTask = host.CaptureAsync();
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        var start = Environment.TickCount64;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (Environment.TickCount64 - start > 6000) throw new TimeoutException("二级菜单截图没有完成");
                if (stage == 0)
                {
                    Send(new HotkeySpec(ModifierKeys.None, Key.Down));
                    Send(new HotkeySpec(ModifierKeys.None, Key.Right));
                    stage++;
                }
                else if (stage == 1)
                {
                    Check(GetMenuItemRect(new WindowInteropHelper(window).Handle, submenu, 0, out rect)
                        && rect.Right > rect.Left && rect.Bottom > rect.Top,
                        $"二级菜单未真正展开：目标={new WindowInteropHelper(window).Handle}，前台={Win32.GetForegroundWindow()}，菜单宽高={rect.Right - rect.Left}×{rect.Bottom - rect.Top}");
                    expected = ScreenCapture.Grab(rect) ?? throw new InvalidOperationException("无法抓取测试子菜单");
                    stage++;
                    Send(spec, repeats: 3);
                }
                else if (Application.Current.Windows.OfType<CaptureOverlay>().FirstOrDefault() is { } overlay)
                {
                    const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                    var shot = (CapturedImage)typeof(CaptureOverlay).GetField("_shot", fields)!.GetValue(overlay)!;
                    var screen = (RECT)typeof(CaptureOverlay).GetField("_screen", fields)!.GetValue(overlay)!;
                    for (var row = 0; row < expected!.Height; row++)
                    {
                        var offset = (rect.Top - screen.Top + row) * shot.Stride + (rect.Left - screen.Left) * 4;
                        Check(shot.Pixels.AsSpan(offset, expected.Stride)
                            .SequenceEqual(expected.Pixels.AsSpan(row * expected.Stride, expected.Stride)),
                            "截图前菜单已消失或菜单像素变化");
                    }
                    overlay.Close();
                    complete = true;
                    timer.Stop();
                    EndMenu();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                timer.Stop();
                EndMenu();
            }
        };
        try
        {
            window.Show(); window.Activate(); Pump();
            FocusTarget(window);
            timer.Start();
            TrackPopupMenuEx(menu, 0x0100, 140, 150, new WindowInteropHelper(window).Handle, IntPtr.Zero);
            Wait(() => complete || failure is not null, "菜单退出后截图未显示");
            if (failure is not null) throw failure;
            Wait(() => captureTask?.IsCompleted == true, "截图取消后没有正常收尾");
            captureTask!.GetAwaiter().GetResult();
            Check(count == 1 && fromHook, "截图未通过钩子触发或一次按键触发多次");
            Console.WriteLine($"       原生二级菜单 + {spec}：一次触发，截图中的菜单逐像素一致");
        }
        finally
        {
            timer.Stop(); EndMenu();
            foreach (var overlay in Application.Current.Windows.OfType<CaptureOverlay>().ToArray()) overlay.Close();
            DestroyMenu(menu);
            window.Close();
            Win32.SetCursorPos(originalCursor.X, originalCursor.Y);
            if (foreground != IntPtr.Zero) Win32.SetForegroundWindow(foreground);
        }
    }

    static HotkeySpec AvailableKey()
    {
        using var message = new MessageWindow("CaptureHotkey.Available");
        var settings = AppSettings.CreateDefault();
        foreach (var key in Enumerable.Range(1, 24))
        {
            var spec = HotkeySpec.Parse("F" + key);
            if (HotkeyManager.FindConflict(settings, HotkeyAction.CaptureOcr, spec.ToString()) is not null) continue;
            if (!Win32.RegisterHotKey(message.Handle, 0x7100, spec.Win32Modifiers, spec.VirtualKey)) continue;
            Win32.UnregisterHotKey(message.Handle, 0x7100);
            return spec;
        }
        throw new InvalidOperationException("没有可用于测试的空闲功能键");
    }

    static void Send(HotkeySpec spec, int repeats = 1)
    {
        var modifiers = new List<ushort>();
        if (spec.Modifiers.HasFlag(ModifierKeys.Control)) modifiers.Add(Win32.VK_CONTROL);
        if (spec.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers.Add(Win32.VK_MENU);
        if (spec.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers.Add(Win32.VK_SHIFT);
        if (spec.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers.Add(Win32.VK_LWIN);
        var inputs = modifiers.Select(k => Input(k, false)).ToList();
        inputs.AddRange(Enumerable.Repeat(Input((ushort)spec.VirtualKey, false), repeats));
        inputs.Add(Input((ushort)spec.VirtualKey, true));
        inputs.AddRange(modifiers.AsEnumerable().Reverse().Select(k => Input(k, true)));
        Check(Win32.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()) == inputs.Count,
            "无法发送完整的键盘输入");
    }

    static INPUT Input(ushort key, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = key, dwFlags = up ? Win32.KEYEVENTF_KEYUP : 0 } },
    };

    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    static void FocusTarget(Window window)
    {
        // 先核对点击命中自己的测试窗口，再送按键，避免前台锁或其他置顶窗口干扰菜单验证。
        var point = window.PointToScreen(new Point(window.ActualWidth / 2, window.ActualHeight / 2));
        var position = new POINT { X = (int)point.X, Y = (int)point.Y };
        var target = new WindowInteropHelper(window).Handle;
        Wait(() => Win32.GetAncestor(Win32.WindowFromPoint(position), Win32.GA_ROOT) == target,
            "截图热键测试窗口被其他窗口遮住");
        Win32.SetCursorPos(position.X, position.Y);
        INPUT[] click = [new INPUT { type = Win32.INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0002 } } },
            new INPUT { type = Win32.INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0004 } } }];
        var released = false;
        MouseButtonEventHandler onReleased = (_, _) => released = true;
        window.PreviewMouseLeftButtonUp += onReleased;
        try
        {
            Check(Win32.SendInput(2, click, Marshal.SizeOf<INPUT>()) == 2, "不能激活截图热键测试窗口");
            // 已是前台也要等这次点击完成，不能把还在排队的点击带进随后打开的原生菜单。
            Wait(() => released, "截图热键测试的焦点点击尚未处理完成");
        }
        finally { window.PreviewMouseLeftButtonUp -= onReleased; }
        Wait(() => Win32.GetForegroundWindow() == target, "截图热键测试窗口未取得焦点");
    }

    static void Wait(Func<bool> done, string message)
    {
        var until = Environment.TickCount64 + 7000;
        while (!done() && Environment.TickCount64 < until) { Pump(); Thread.Sleep(10); }
        Check(done(), message);
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr item, string text);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool EndMenu();
    [DllImport("user32.dll")] static extern bool GetMenuItemRect(IntPtr window, IntPtr menu, uint item, out RECT rect);
    [DllImport("user32.dll")]
    static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
}
