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
        var target = new Window { Content = new TextBox(), Width = 300, Height = 120, ShowInTaskbar = false };
        var foreground = Win32.GetForegroundWindow();
        try
        {
            target.Show(); target.Activate(); Pump();
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
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        var foreground = Win32.GetForegroundWindow();
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
                        && rect.Right > rect.Left && rect.Bottom > rect.Top, "二级菜单未真正展开");
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
            if (foreground != IntPtr.Zero) Win32.SetForegroundWindow(foreground);
        }
    }

    static HotkeySpec AvailableKey()
    {
        using var message = new MessageWindow("CaptureHotkey.Available");
        foreach (var key in Enumerable.Range(1, 24))
        {
            var spec = HotkeySpec.Parse("F" + key);
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
