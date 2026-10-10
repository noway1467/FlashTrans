using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

static class ProtectionProbe
{
    internal static void RunAll(Action<string, Action> step)
    {
        step("保护：富文本/文件列表/自定义格式剪贴板完整往返", () => WithClipboard(ClipboardFormats));
        step("保护：图片像素、空剪贴板及新复制内容不被覆盖", () => WithClipboard(ClipboardImagesAndRace));
        step("保护：真实 WPF 取词、取消收尾和排除应用", () => WithClipboard(SelectionRoundtrip));
        step("保护：应用名称精确匹配与配置迁移往返", ExclusionSettings);
        step("贴图：真实窗口缩放、原尺寸、透明度与穿透恢复", PinInteraction);
    }
    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    static uint _ownedSequence;
    static void Owned() => _ownedSequence = Win32.GetClipboardSequenceNumber();
    static void SetText(string text) { Clipboard.SetText(text); Owned(); }
    static void WithClipboard(Action action)
    {
        using var original = ClipboardSnapshot.Capture();
        _ownedSequence = original.Sequence;
        try { action(); }
        finally { original.Restore(_ownedSequence); }
    }
    static void SeedFormats()
    {
        var data = new DataObject();
        data.SetText("original text", TextDataFormat.UnicodeText);
        data.SetText("<b>original html</b>", TextDataFormat.Html);
        data.SetText(@"{\rtf1 original rich text}", TextDataFormat.Rtf);
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { Environment.ProcessPath! });
        data.SetData("FlashTrans.Test.Custom", new System.IO.MemoryStream([3, 1, 4, 1, 5]));
        Clipboard.SetDataObject(data, true); Owned();
    }
    static void CheckFormats()
    {
        for (var attempt = 0; ; attempt++)
        {
            try { CheckFormatsCore(); return; }
            // 系统剪贴板历史/查看器可能短暂持锁；只重试明确的占用错误，内容不符仍立即失败。
            catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x800401D0 && attempt < 20)
            { Thread.Sleep(25); }
        }
    }
    static void CheckFormatsCore()
    {
        Check(Clipboard.GetText() == "original text", "纯文本未恢复");
        Check(Clipboard.GetText(TextDataFormat.Html).Contains("original html"), "HTML 未恢复");
        Check(Clipboard.GetText(TextDataFormat.Rtf).Contains("original rich text"), "RTF 未恢复");
        Check(Clipboard.GetFileDropList().Contains(Environment.ProcessPath!), "文件列表未恢复");
        using var bytes = Clipboard.GetData("FlashTrans.Test.Custom") as System.IO.MemoryStream;
        Check(bytes is not null && bytes.ToArray().SequenceEqual(new byte[] { 3, 1, 4, 1, 5 }), "自定义格式未恢复");
    }
    static void ClipboardFormats()
    {
        SeedFormats(); using var snapshot = ClipboardSnapshot.Capture(); SetText("translation");
        Check(snapshot.Restore(_ownedSequence), "恢复未执行"); Owned(); CheckFormats();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(ProtectionProbe).Assembly.Location);
        start.ArgumentList.Add("--read-clipboard-child");
        using var child = Process.Start(start)!;
        var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(15000)) { child.Kill(true); child.WaitForExit(); throw new TimeoutException("外部进程读取剪贴板超时"); }
        Check(child.ExitCode == 0, "外部进程无法读取恢复后的剪贴板：" + error.GetAwaiter().GetResult());
    }

    internal static int ReadClipboardChild()
    {
        try { CheckFormats(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static void ClipboardImagesAndRace()
    {
        Console.WriteLine("       图片备份/恢复");
        var pixels = new byte[] { 4, 8, 12, 255, 16, 32, 48, 255 };
        SelectionReader.SetImageAndText(new CapturedImage(2, 1, pixels), "image"); Owned();
        using (var image = ClipboardSnapshot.Capture())
        {
            SetText("temporary"); Check(image.Restore(_ownedSequence), "图片恢复失败"); Owned();
            var bitmap = Clipboard.GetImage()!;
            var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var actual = new byte[8]; bgra.CopyPixels(actual, 8, 0);
            Check(actual.SequenceEqual(pixels) && Clipboard.GetText() == "image", "图片像素或附带文本丢失");
        }
        using (var old = ClipboardSnapshot.Capture())
        {
            Console.WriteLine("       并发复制保护");
            SetText("selection"); var expected = _ownedSequence; SetText("new user copy");
            Check(!old.Restore(expected) && Clipboard.GetText() == "new user copy", "覆盖了更新的剪贴板");
        }
        Clipboard.Clear(); Owned();
        Console.WriteLine("       空剪贴板恢复");
        using var empty = ClipboardSnapshot.Capture(); SetText("selection");
        Check(empty.Restore(_ownedSequence), "空剪贴板未恢复"); Owned();
        Check(!Clipboard.ContainsText(), "空剪贴板变成了选区文本");
    }
    static void Pump(Task? task = null)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(8);
            if (DateTime.UtcNow > until) throw new TimeoutException("保护自测超时");
        } while (task is not null && !task.IsCompleted);
        task?.GetAwaiter().GetResult();
    }
    static void SelectionRoundtrip()
    {
        var previousWindow = Win32.GetForegroundWindow();
        var settings = SettingsService.Instance.Current; var excluded = settings.ExcludedApplications;
        var box = new TextBox { Text = "selected probe text", FontSize = 20 };
        var window = new Window { Title = "FlashTrans 隔离取词测试", Width = 420, Height = 130, Content = box, Topmost = true };
        try
        {
            settings.ExcludedApplications = [];
            window.Show();
            var targetHandle = new WindowInteropHelper(window).Handle;
            void FocusSelection()
            {
                // WPF/OLE 初始化可能延迟焦点消息；只等待前置条件，不重跑失败的取词断言。
                var deadline = DateTime.UtcNow.AddSeconds(3);
                var clicked = false;
                do
                {
                    window.Activate(); Win32.SetForegroundWindow(targetHandle);
                    if (!clicked && Win32.GetForegroundWindow() != targetHandle)
                    {
                        // Windows 会拒绝后台程序抢前台。像用户一样点击自己的测试窗口，
                        // 只在原生命中确认属于本窗口且没有正在按鼠标时执行，随后恢复鼠标位置。
                        Win32.GetWindowRect(targetHandle, out var bounds);
                        var point = new POINT { X = bounds.Left + 60, Y = bounds.Top + 60 };
                        if (Win32.GetAncestor(Win32.WindowFromPoint(point), Win32.GA_ROOT) == targetHandle &&
                            (Win32.GetAsyncKeyState(1) & 0x8000) == 0)
                        {
                            Win32.GetCursorPos(out var previousMouse);
                            try
                            {
                                Win32.SetCursorPos(point.X, point.Y);
                                if (Win32.GetAncestor(Win32.WindowFromPoint(point), Win32.GA_ROOT) != targetHandle)
                                    throw new InvalidOperationException("测试窗口已被遮挡，取消模拟点击");
                                var down = new INPUT { type = Win32.INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0002 } } };
                                var up = new INPUT { type = Win32.INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0004 } } };
                                Win32.SendInput(2, [down, up], System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
                                Pump(Task.Delay(80));
                            }
                            finally { Win32.SetCursorPos(previousMouse.X, previousMouse.Y); }
                            clicked = true;
                        }
                    }
                    Keyboard.Focus(box); box.SelectAll(); Pump(Task.Delay(100));
                    if (Win32.GetForegroundWindow() == targetHandle && box.IsKeyboardFocused && box.SelectionLength == box.Text.Length) return;
                } while (DateTime.UtcNow < deadline);
                throw new InvalidOperationException($"测试窗口没有稳定取得焦点/选区，不能发送真实 Ctrl+C：前台={Win32.GetForegroundWindow() == targetHandle}，激活={window.IsActive}，焦点={box.IsKeyboardFocused}，选区={box.SelectionLength}/{box.Text.Length}");
            }
            SeedFormats(); FocusSelection();
            var task = SelectionReader.GetSelectedTextAsync(true); Pump(task); Owned();
            Check(task.Result == "selected probe text", "真实 Ctrl+C 取词失败：" + (SelectionReader.LastError ?? SelectionReader.LastAbortReason ?? "未读回选区")); CheckFormats();
            using var cancel = new CancellationTokenSource();
            box.PreviewKeyDown += (_, e) => { if (e.Key == Key.C) cancel.Cancel(); };
            FocusSelection();
            task = SelectionReader.GetSelectedTextAsync(true, cancel.Token); Pump(task); Owned();
            Check(task.Result is null && cancel.IsCancellationRequested, "取消未触发"); CheckFormats();
            using (var locked = new ManualResetEventSlim())
            using (var unlock = new ManualResetEventSlim())
            {
                Exception? lockFailure = null;
                var locker = new Thread(() =>
                {
                    try
                    {
                        using var owner = new HwndSource(new HwndSourceParameters("FlashTrans clipboard contention")
                        { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 1, Height = 1 });
                        Check(ClipboardSnapshot.Open(owner.Handle), "无法建立剪贴板竞争样例");
                        try { locked.Set(); Check(unlock.Wait(5000), "竞争样例未被释放"); }
                        finally { Win32.CloseClipboard(); }
                    }
                    catch (Exception ex) { lockFailure = ex; locked.Set(); }
                }) { IsBackground = true };
                locker.SetApartmentState(ApartmentState.STA); locker.Start();
                try
                {
                    Check(locked.Wait(2000), "竞争样例未启动");
                    if (lockFailure is not null) throw lockFailure;
                    var beforeLocked = Win32.GetClipboardSequenceNumber();
                    task = SelectionReader.GetSelectedTextAsync(true); Pump(task);
                    Check(task.Result is null && SelectionReader.LastError is not null && beforeLocked == Win32.GetClipboardSequenceNumber(),
                        "占用保护失败：" + (SelectionReader.LastError ?? SelectionReader.LastAbortReason ?? "未报告占用"));
                }
                finally { unlock.Set(); Check(locker.Join(6000), "竞争线程未退出"); }
                if (lockFailure is not null) throw lockFailure;
            }
            CheckFormats();
            settings.ExcludedApplications = [Process.GetCurrentProcess().ProcessName + ".exe"];
            var before = Win32.GetClipboardSequenceNumber();
            task = SelectionReader.GetSelectedTextAsync(true); Pump(task);
            Check(task.Result is null && before == Win32.GetClipboardSequenceNumber(), "排除应用仍改写剪贴板");
        }
        finally
        {
            settings.ExcludedApplications = excluded; window.Close();
            Win32.SetForegroundWindow(previousWindow);
        }
    }
    static void ExclusionSettings()
    {
        Check(ApplicationExclusions.Matches("NOTEPAD", ["notepad.exe"]), "大小写/扩展名匹配失败");
        Check(!ApplicationExclusions.Matches("notepad++", ["notepad.exe"]), "应用名称被子串误匹配");
        var settings = System.Text.Json.JsonSerializer.Deserialize("{\"version\":9,\"excludedApplications\":null}", SettingsJson.Default.AppSettings)!;
        Check(SettingsService.Migrate(settings) && settings.Version == AppSettings.CurrentVersion, "配置未迁移");
        settings.ExcludedApplications = [" notepad ", "NOTEPAD.exe", "*.exe"];
        settings.DocumentRememberProgress = true; settings.DocumentBilingualOutput = true;
        settings.SourceLang = "ja"; settings.DocumentSourceLang = "auto";
        SettingsService.Normalize(settings);
        Check(settings.DocumentSourceLang == "auto", "文件源语言的自动检测被重置为主窗口语言");
        Check(settings.ExcludedApplications.Count == 1, "排除应用归一化失败");
        var copy = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(settings, SettingsJson.Default.AppSettings), SettingsJson.Default.AppSettings)!;
        Check(copy.DocumentRememberProgress && copy.DocumentBilingualOutput && copy.ExcludedApplications.Count == 1, "新配置读写失败");
    }
    static void PinInteraction()
    {
        var previousWindow = Win32.GetForegroundWindow();
        var pixels = Enumerable.Repeat((byte)255, 320 * 180 * 4).ToArray();
        var window = new PinnedShotWindow(new CapturedImage(320, 180, pixels), new RECT { Left = 70, Top = 70, Right = 390, Bottom = 250 });
        try
        {
            window.Show(); Pump();
            var handle = new WindowInteropHelper(window).Handle;
            window.SetZoom(2); Pump(); Win32.GetWindowRect(handle, out var rect);
            Check(rect.Right - rect.Left == 640 && rect.Bottom - rect.Top == 360, "贴图没有按物理像素缩放");
            window.SetZoom(1); Pump(); Win32.GetWindowRect(handle, out rect);
            Check(rect.Right - rect.Left == 320, "原尺寸恢复失败");
            window.SetOpacity(0.5); Check(window.Opacity == 0.5, "透明度未应用");
            window.SetOpacity(0); Check(window.Opacity == 0.2, "贴图变成完全不可见");
            window.SetClickThrough(true);
            Check(window.ClickThrough && (Win32.GetWindowLong(handle, Win32.GWL_EXSTYLE) & Win32.WS_EX_TRANSPARENT) != 0, "原生穿透标记未应用");
            var hit = Win32.WindowFromPoint(new POINT { X = rect.Left + 40, Y = rect.Top + 40 });
            Check(Win32.GetAncestor(hit, Win32.GA_ROOT) != handle, "穿透后原生命中测试仍落在贴图");
            var release = new List<int>();
            void Key(int key, bool up)
            {
                var input = new INPUT { type = Win32.INPUT_KEYBOARD, u = new InputUnion
                { ki = new KEYBDINPUT { wVk = (ushort)key, dwFlags = up ? Win32.KEYEVENTF_KEYUP : 0 } } };
                Win32.SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
            }
            try
            {
                foreach (var key in new[] { Win32.VK_CONTROL, Win32.VK_MENU })
                    if ((Win32.GetAsyncKeyState(key) & 0x8000) == 0) { Key(key, false); release.Add(key); }
                var until = DateTime.UtcNow.AddSeconds(2);
                while (window.ClickThrough && DateTime.UtcNow < until) Pump();
                Check(!window.ClickThrough, "Ctrl+Alt 无法恢复贴图交互");
            }
            finally { foreach (var key in release.AsEnumerable().Reverse()) Key(key, true); }
            window.SetClickThrough(false);
            Check(!window.ClickThrough && (Win32.GetWindowLong(handle, Win32.GWL_EXSTYLE) & Win32.WS_EX_TRANSPARENT) == 0, "穿透无法恢复");
        }
        finally { window.Close(); Win32.SetForegroundWindow(previousWindow); }
    }
}
