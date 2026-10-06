using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Providers;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

/// <summary>本次翻译源配置与截图生命周期回归，只使用隔离配置和回环 HTTP 服务。</summary>
static class SourceCaptureProbe
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    const string ModelBody = "{\"data\":[{\"id\":\"z-model\"},{\"id\":\"a-model\"},{\"id\":\"a-model\"},{\"id\":\" \"},{\"id\":12},null]}";

    internal static void RunAll(Action<string, Action> step)
    {
        var original = SettingsService.Instance.Current;
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            SettingsService.Instance.Apply(new AppSettings { Providers = [], AggregateTab = true });
            step("AI 源：名称首字符及完整 Unicode 徽标", Badges);
            step("AI 源：模型地址、Bearer 鉴权、去重与无 Key 本地接口", Models);
            step("AI 源：权限、空列表、错误响应、超时和取消", ModelErrors);
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                step($"AI 源：{theme} 菜单置首、模型手填/选择/保留、过期请求", () => Editor(theme));
                step($"AI 源：{theme} 设置、等待及结果卡片徽标一致", () => Cards(theme));
            }
            step("截图快捷键：取消时保留译文窗口，背景快照包含窗口", () => Capture(CaptureAction.None, false));
            step("截图快捷键：钉住完成后保留译文窗口", () => Capture(CaptureAction.Pin, false));
            step("截图快捷键：原先收起的窗口保持收起", () => Capture(CaptureAction.None, true));
        }
        finally
        {
            SettingsService.Instance.Apply(original);
            ThemeService.Apply(original);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void Badges()
    {
        foreach (var (name, expected) in new[] { ("B站翻译", "B"), (" AI 翻译 副本", "A"),
                     (" 智谱", "智"), ("deepseek", "D"), ("", "A"), ("👩‍💻模型", "👩‍💻"), ("e\u0301模型", "E\u0301") })
        {
            var badge = ProviderMeta.BadgeFor(ProviderKind.OpenAiCompat, name);
            Check(badge == expected, "AI 名称首字符错误");
            Check(((TextBlock)UiKit.Badge(badge, "#10A37F").Child).Text == expected, "徽标控件截断完整字符");
        }
        Check(ProviderMeta.BadgeFor(ProviderKind.GoogleFree, "自定义") == "G", "非 AI 品牌徽标被改动");
    }

    static ProviderConfig Config(string url, string key = "test-model-key")
    {
        var cfg = ProviderConfig.Create(ProviderKind.OpenAiCompat, "B站翻译");
        cfg.Options["baseUrl"] = url;
        cfg.Options["apiKey"] = key;
        cfg.Options["model"] = "manual-model";
        cfg.TimeoutMs = 2000;
        return cfg;
    }

    static void Models()
    {
        using var server = new ModelServer();
        foreach (var (suffix, path) in new[] { ("", "/v1/models"), ("/v1/", "/v1/models"),
                     ("/v1/chat/completions/", "/v1/models"), ("/api/paas/v4", "/api/paas/v4/models"),
                     ("/gateway/v1/models", "/gateway/v1/models") })
        {
            var cfg = Config(" " + server.Url + suffix + " ", " test-model-key ");
            cfg.Options["model"] = "";
            var list = Await(new OpenAiCompatTranslator(cfg).ListModelsAsync(default));
            Check(list.SequenceEqual(new[] { "a-model", "z-model" }), "模型未过滤、去重或排序");
            Check(server.Requests.TryDequeue(out var request) && request.Path == path &&
                request.Authorization == "Bearer test-model-key", "模型请求路径或鉴权头错误");
        }
        Await(new OpenAiCompatTranslator(Config(server.Url, "")).ListModelsAsync(default));
        Check(server.Requests.TryDequeue(out var local) && local.Authorization == "", "无 Key 本地请求附加了鉴权头");
        foreach (var bad in new[] { "", "ftp://host", "https://user:pass@host/v1", "https://host/v1?key=secret" })
            ExpectError(new OpenAiCompatTranslator(Config(bad)).ListModelsAsync(default), "接口地址");
        Check(OpenAiCompatTranslator.ApiEndpoint(server.Url, "chat/completions") == server.Url + "/v1/chat/completions",
            "仅填域名后翻译与模型地址不一致");
    }

    static void ModelErrors()
    {
        using var server = new ModelServer();
        foreach (var code in new[] { 401, 403, 404, 405, 429, 500 })
        {
            server.Status = code;
            server.Body = "test-model-key sensitive-server-body";
            var error = ExpectError(new OpenAiCompatTranslator(Config(server.Url)).ListModelsAsync(default), $"HTTP {code}");
            Check(!error.Contains("test-model-key") && !error.Contains("sensitive-server-body"), "错误提示泄露服务原文");
        }
        server.Status = 200;
        foreach (var body in new[] { "<html>private body</html>", "{\"error\":\"private body\"}", "[]" })
        {
            server.Body = body;
            Check(!ExpectError(new OpenAiCompatTranslator(Config(server.Url)).ListModelsAsync(default), "模型列表")
                .Contains("private body"), "无效响应原文被展示");
        }
        server.Body = "{\"data\":[]}";
        Check(Await(new OpenAiCompatTranslator(Config(server.Url)).ListModelsAsync(default)).Count == 0, "空列表处理失败");
        server.Body = new string('x', 2 * 1024 * 1024 + 1);
        ExpectError(new OpenAiCompatTranslator(Config(server.Url)).ListModelsAsync(default), "2 MB");
        server.Body = ModelBody;
        server.DelayMs = 1600;
        var slow = Config(server.Url); slow.TimeoutMs = 800;
        ExpectError(new OpenAiCompatTranslator(slow).ListModelsAsync(default), "超时");
        using var cancel = new CancellationTokenSource(80);
        try { Await(new OpenAiCompatTranslator(Config(server.Url)).ListModelsAsync(cancel.Token)); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
        throw new InvalidOperationException("请求取消未传播");
    }

    static string ExpectError(Task<IReadOnlyList<string>> task, string part)
    {
        try { Await(task); }
        catch (ProviderException ex) { Check(ex.Message.Contains(part), "错误分类不正确：" + ex.Message); return ex.Message; }
        throw new InvalidOperationException("异常响应却被当成成功");
    }

    static void Editor(AppTheme theme)
    {
        ThemeService.ApplyTheme(theme);
        using var server = new ModelServer();
        var cfg = Config(server.Url);
        SettingsService.Instance.Current.Providers = [cfg];
        var window = new SettingsWindow(new AppHost());
        try
        {
            Show(window);
            window.SelectTab("sources"); window.UpdateLayout();
            var add = Descendants<Button>(window).Single(b => b.Content is "＋ 添加源");
            Click(add);
            var menu = ContextMenuService.GetContextMenu(add);
            try
            {
                var kinds = menu.Items.OfType<MenuItem>().Where(i => i.Tag is ProviderKind).Select(i => (ProviderKind)i.Tag).ToArray();
                Check(kinds.Length == ProviderMeta.All.Length && kinds.Distinct().Count() == kinds.Length, "添加菜单漏源或重复");
                Check(kinds[0] == ProviderKind.OpenAiCompat && kinds.Take(3).All(k => ProviderMeta.Get(k).IsAi), "AI 没有置于最前");
                SavePreview(menu, $"add-source-{theme}");
            }
            finally { menu.IsOpen = false; }
            typeof(SettingsWindow).GetMethod("ToggleExpand", Private)!.Invoke(window, [cfg.Id]);
            window.UpdateLayout();
            var nameEditor = Descendants<TextBox>(window).Single(t => t.Text == "B站翻译");
            nameEditor.Text = "智谱测试";
            Check(HasBadge(window, "智"), "修改名称后徽标未即时更新");
            var models = Descendants<ComboBox>(window).Single(c => c.Name == "AiModelSelector");
            var edit = (TextBox)models.Template.FindName("PART_EditableTextBox", models);
            var fetch = Descendants<Button>(window).Single(b => b.Content is "拉取模型");
            var status = Descendants<TextBlock>(window).Single(t => t.Name == "AiModelStatus");
            Check(edit.Visibility == Visibility.Visible, "模型下拉框不能手动输入");
            edit.Text = "typed-model";
            Check(cfg.Options["model"] == "typed-model", "手动模型未写回配置");
            Click(fetch); Wait(() => fetch.IsEnabled);
            Check(models.Items.Count == 2 && models.Text == "typed-model" && cfg.Options["model"] == "typed-model", "拉取覆盖了手动模型");
            if (Environment.GetCommandLineArgs().Contains("--shot"))
            {
                window.Height = 900; window.UpdateLayout();
                models.BringIntoView(); Drain();
                SavePreview(window, $"ai-models-{theme}");
            }
            models.IsDropDownOpen = true; window.UpdateLayout();
            models.SelectedIndex = 1; models.IsDropDownOpen = false; Drain();
            Check(cfg.Options["model"] == "z-model", "选择模型未保存");
            edit.Text = "new-custom";
            Check(cfg.Options["model"] == "new-custom", "选择后无法重新手填");
            server.Status = 403;
            Click(fetch); Wait(() => fetch.IsEnabled);
            Check(status.Text.Contains("403") && cfg.Options["model"] == "new-custom", "权限错误无提示或清空模型");
            server.Status = 200; server.DelayMs = 180;
            Click(fetch); cfg.Options["apiKey"] = "changed-test-key";
            Wait(() => fetch.IsEnabled);
            Check(status.Text.Contains("已更改") && cfg.Options["model"] == "new-custom", "过期模型响应覆盖配置");
            Click(fetch); window.SelectTab("general"); Drain();
            Wait(() => fetch.IsEnabled);
            Check(cfg.Options["model"] == "new-custom", "编辑器卸载后仍修改模型");
        }
        finally { window.Close(); SettingsService.Instance.Current.Providers = []; }
    }

    static void Cards(AppTheme theme)
    {
        ThemeService.ApplyTheme(theme);
        var cfg = Config("http://127.0.0.1:1/v1");
        SettingsService.Instance.Current.Providers = [cfg];
        var settings = new SettingsWindow(new AppHost());
        var view = new ResultView();
        var window = new Window { Content = view, Width = 480, Height = 280 };
        try
        {
            Show(settings); settings.SelectTab("sources"); settings.UpdateLayout();
            Check(HasBadge(settings, "B"), "设置卡未显示名称首字");
            Show(window);
            var batch = new TranslateBatch { SourceText = "offline" };
            view.BeginAggregate(batch, [cfg]); window.UpdateLayout();
            Check(HasBadge(view, "B"), "等待卡片未显示名称首字");
            view.UpdateOne(new TranslateResult { ProviderId = cfg.Id, ProviderName = cfg.DisplayName });
            window.UpdateLayout(); Check(HasBadge(view, "B"), "结果卡片未显示名称首字");
        }
        finally { settings.Close(); window.Close(); SettingsService.Instance.Current.Providers = []; }
    }

    static bool HasBadge(DependencyObject root, string text) => Descendants<Border>(root)
        .Any(b => b.Width == 18 && b.Height == 18 && b.Child is TextBlock t && t.Text == text);

    static void SavePreview(FrameworkElement element, string name)
    {
        if (!Environment.GetCommandLineArgs().Contains("--shot")) return;
        element.UpdateLayout(); Drain();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),
            (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("shots");
        using var output = File.Create(Path.Combine("shots", name + ".png"));
        png.Save(output);
    }

    static void Capture(CaptureAction action, bool stashed)
    {
        SettingsService.Instance.Current.Providers = [];
        var before = Application.Current.Windows.Cast<Window>().ToHashSet();
        var host = new AppHost();
        PopupWindow? popup = null;
        Exception? failure = null;
        var observed = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        try
        {
            host.ShowPopupFor("capture-state-sentinel", new Point(160, 140));
            popup = Application.Current.Windows.OfType<PopupWindow>().Single(w => !before.Contains(w));
            // 用已知纯色标记验证真实桌面快照，而不是只断言 IsVisible。
            popup.Content = new Border { Background = Brushes.Magenta, MinHeight = 120 };
            popup.UpdateLayout(); popup.Activate(); Drain();
            var sample = popup.PointToScreen(new Point(popup.ActualWidth / 2, popup.ActualHeight / 2));
            if (stashed) popup.StashPopup();
            timer.Tick += (_, _) =>
            {
                var overlay = Application.Current.Windows.OfType<CaptureOverlay>().FirstOrDefault(w => !before.Contains(w));
                if (overlay is null) return;
                timer.Stop(); observed = true;
                try
                {
                    Check(popup.IsVisible == !stashed && popup.CanRestore == stashed, "进入截图时弹窗状态被改变");
                    if (!stashed)
                    {
                        var shot = (CapturedImage)typeof(CaptureOverlay).GetField("_shot", Private)!.GetValue(overlay)!;
                        var screen = (RECT)typeof(CaptureOverlay).GetField("_screen", Private)!.GetValue(overlay)!;
                        var pixel = ((int)sample.Y - screen.Top) * shot.Stride + ((int)sample.X - screen.Left) * 4;
                        Check(shot.Pixels[pixel] > 240 && shot.Pixels[pixel + 1] < 20 && shot.Pixels[pixel + 2] > 240,
                            "截图快照未包含翻译窗口");
                    }
                    if (action != CaptureAction.None)
                        typeof(CaptureOverlay).GetField("_result", Private)!.SetValue(overlay,
                            new CaptureOutcome(action, new CapturedImage(16, 16, new byte[16 * 16 * 4]), default));
                }
                catch (Exception ex) { failure = ex; }
                finally { overlay.Close(); }
            };
            timer.Start();
            typeof(AppHost).GetMethod("OnHotkey", Private)!.Invoke(host, [HotkeyAction.CaptureOcr]);
            Wait(() => observed && !(bool)typeof(AppHost).GetField("_capturing", Private)!.GetValue(host)!);
            if (failure is not null) throw failure;
            Check(popup.IsVisible == !stashed && popup.CanRestore == stashed, "截图完成后弹窗状态被改变");
            Check((string)typeof(PopupWindow).GetField("_text", Private)!.GetValue(popup)! == "capture-state-sentinel", "截图改变译文内容");
            if (action == CaptureAction.Pin)
                Check(Application.Current.Windows.OfType<PinnedShotWindow>().Any(w => !before.Contains(w) && w.IsVisible), "截图完成未产生贴图");
        }
        finally
        {
            timer.Stop();
            foreach (var window in Application.Current.Windows.Cast<Window>().Where(w => !before.Contains(w)).ToArray()) window.Close();
        }
    }

    static void Show(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -4000; window.Top = -4000; window.Show(); window.UpdateLayout(); Drain();
    }
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    static T Await<T>(Task<T> task) { Wait(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("定向回归等待超时");
            Drain(); Thread.Sleep(10);
        }
    }
    static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    sealed class ModelServer : IDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _stop = new();
        readonly Task _loop;
        public readonly ConcurrentQueue<(string Path, string Authorization)> Requests = new();
        public string Url { get; }
        public int Status = 200;
        public string Body = ModelBody;
        public int DelayMs;
        public ModelServer()
        {
            _listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = Task.Run(async () =>
            {
                var clients = new List<Task>();
                try
                {
                    while (!_stop.IsCancellationRequested)
                        clients.Add(Serve(await _listener.AcceptTcpClientAsync(_stop.Token)));
                }
                catch (OperationCanceledException) { }
                finally { await Task.WhenAll(clients); }
            });
        }
        async Task Serve(TcpClient client)
        {
            using (client)
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var first = await reader.ReadLineAsync(_stop.Token) ?? "";
                string auth = "";
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                    if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) auth = line[14..].Trim();
                Requests.Enqueue((first.Split(' ')[1], auth));
                var body = Encoding.UTF8.GetBytes(Body); var status = Status;
                await Task.Delay(DelayMs, _stop.Token);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, _stop.Token); await stream.WriteAsync(body, _stop.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
        public void Dispose()
        {
            _stop.Cancel(); _listener.Stop(); _loop.GetAwaiter().GetResult(); _stop.Dispose();
        }
    }
}
