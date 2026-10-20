using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlashTrans.Core;
using FlashTrans.Services;
using FlashTrans.Views;

namespace FlashTrans.SelfTest;

/// <summary>仅使用本机 HTTP/SSE 接口，覆盖真实引擎调度、缓存和主窗口降级流程。</summary>
static class TranslationProbe
{
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    const string Text = "A local translation regression sentence.";

    public static void RunAll(Action<string, Action> step)
    {
        step("翻译：多目标部分失败保留译文、错误和失败状态", PartialTargets);
        step("翻译：多目标部分失败自动换源", PartialFallback);
        step("翻译：聚合回调也标明部分失败", AggregatePartialTargets);
        step("翻译：部分失败重试补齐缺失目标并恢复健康状态", PartialRecovery);
        step("翻译：批量接口静默漏目标语言不能算成功", MissingBatchTarget);
        step("翻译：空白译文失败且不写缓存", BlankTranslation);
        step("翻译：已有目标缓存不掩盖新目标失败", CachedPartialTarget);
        step("翻译：模型、提示词、接口地址和密钥变更使缓存失效", ConfigurationChanges);
        step("翻译：模型和提示词含分隔符时配置签名不碰撞", ConfigurationSeparators);
        step("翻译：外观变更保留缓存，选项重排不误清缓存", UnrelatedSettings);
        step("翻译：配置变更阻止在途旧请求回写缓存", InflightConfigurationChange);
        foreach (var mode in new[] { "done", "choices", "reasoning", "cleanup" })
            step($"翻译：空流 {mode} 经普通重试后自动换源", () => StreamCase(mode));
        step("翻译：流式异常仍能自动换源", () => StreamCase("error"));
        step("翻译：空流普通重试成功时不再换源", () => StreamCase("retry"));
        step("翻译：关闭自动换源时空流只提示错误", () => StreamCase("disabled"));
        step("翻译：正常流式结果不触发重试", () => StreamCase("success"));
        step("翻译：已取消空流不重试、不显示旧结果", () => StreamCase("cancel"));
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void WithSettings(ProviderConfig[] providers, Action<AppSettings, TranslateEngine> test)
    {
        Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")), "翻译自测必须使用便携隔离配置");
        var service = SettingsService.Instance;
        var original = service.Current;
        var engine = TranslateEngine.Instance;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            service.Apply(new AppSettings
            {
                Providers = providers.ToList(), PrimaryProviderId = providers[0].Id,
                SourceLang = "en", TargetLang = "zh-CN", AutoSwapSameLang = false,
                MultiTargets = ["zh-CN", "fr"], AutoFallback = false, AggregateTab = false,
                CacheEnabled = true, ShowDictionary = false, TranslateOnType = false,
                CloseToTray = false, AlwaysOnTop = false,
            });
            engine.Cache.Clear();
            engine.Health.Reset();
            test(service.Current, engine);
        }
        finally
        {
            engine.Cache.Clear();
            engine.Health.Reset();
            service.Apply(original);
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    static ProviderConfig Provider(LocalServer server, ProviderKind kind = ProviderKind.LibreTranslate,
                                   string path = "/primary")
    {
        var cfg = ProviderConfig.Create(kind, path.Trim('/'));
        cfg.Options[kind == ProviderKind.OpenAiCompat ? "baseUrl" : "endpoint"] = server.Url + path;
        cfg.Options["apiKey"] = "local-test-key-a";
        if (kind == ProviderKind.OpenAiCompat) cfg.Options["model"] = "local-test-model";
        return cfg;
    }

    static TranslateBatch Single(TranslateEngine engine, ProviderConfig cfg)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var task = engine.SingleAsync(cfg.Id, Text, false, timeout.Token);
        PumpUntil(task);
        return task.GetAwaiter().GetResult();
    }

    static Response Json(object value) => new(JsonSerializer.Serialize(value));
    static Response Ai(string text) => Json(new { choices = new[] { new { message = new { content = text } } } });

    static Response PartialResponse(Request request)
    {
        var target = request.Json["target"]?.GetValue<string>();
        return request.Path.StartsWith("/primary/", StringComparison.Ordinal) && target == "fr"
            ? Json(new { error = "local French failure" })
            : Json(new { translatedText = "translated-" + target });
    }

    static void PartialTargets()
    {
        using var server = new LocalServer(r => Task.FromResult(PartialResponse(r)));
        var cfg = Provider(server);
        WithSettings([cfg], (s, engine) =>
        {
            s.MultiTargetEnabled = true;
            var batch = Single(engine, cfg);
            var result = batch.Results.Single();
            Check(!result.Ok, "只返回一个目标语言却被当成全部成功");
            Check(result.Get("zh-CN") == "translated-zh" && result.Get("fr") is null, "已成功的译文丢失或失败项被伪造");
            Check(result.Error?.Contains("法语") == true && result.Error.Contains("local French failure"), "没有保留失败语言与原因");
            Check(!engine.Health.LastOk(cfg.Id), "部分失败被记录成源健康成功");

            var view = new ResultView();
            view.ShowBatch(batch, aggregate: false);
            var rendered = Descendants(view).OfType<TextBox>().Select(t => t.Text).ToList();
            var errors = Descendants(view).OfType<TextBlock>().Select(t => t.Text).ToList();
            Check(rendered.Contains("translated-zh"), "部分成功译文在界面上被隐藏");
            Check(errors.Any(t => t.Contains("local French failure")), "界面没有显示部分失败提示");
            Check(ResultView.AllText(batch).Contains("translated-zh"), "复制全部遗漏了可见的部分译文");

            _ = Single(engine, cfg);
            Check(server.Requests.Count(r => r.Json["target"]?.ToString() == "zh") == 1, "重试没有复用已成功目标的缓存");
            Check(server.Requests.Count(r => r.Json["target"]?.ToString() == "fr") == 2, "失败目标被错误缓存");
            Check(engine.Health.IsCoolingDown(cfg.Id), "连续部分失败没有进入冷却");
        });
    }

    static void PartialFallback()
    {
        using var server = new LocalServer(r => Task.FromResult(PartialResponse(r)));
        var primary = Provider(server);
        var backup = Provider(server, path: "/backup");
        WithSettings([primary, backup], (s, engine) =>
        {
            s.MultiTargetEnabled = true;
            s.AutoFallback = true;
            var batch = Single(engine, primary);
            Check(batch.Results.Count == 2 && !batch.Results[0].Ok && batch.Results[1].Ok, "部分失败没有触发降级链");
            Check(batch.Results[1].Texts.Count == 2, "备用源没有覆盖全部目标语言");
            Check(batch.Notes.Any(n => n.Contains("backup")), "没有自动换源提示");
        });
    }

    static void MissingBatchTarget()
    {
        using var server = new LocalServer(r => Task.FromResult(r.Path.StartsWith("/primary/", StringComparison.Ordinal)
            ? Json(new[] { new { translations = new[] { new { to = "zh-Hans", text = "batch-zh" } } } })
            : PartialResponse(r)));
        var primary = Provider(server, ProviderKind.AzureTranslator);
        var backup = Provider(server, path: "/backup");
        WithSettings([primary, backup], (s, engine) =>
        {
            s.MultiTargetEnabled = true;
            s.AutoFallback = true;
            var batch = Single(engine, primary);
            Check(batch.Results.Count == 2 && !batch.Results[0].Ok && batch.Results[1].Ok, "批量源漏目标语言却中断了降级链");
            Check(batch.Results[0].Error?.Contains("法语") == true, "批量缺失结果没有标明语言");
            Check(batch.Results[0].Get("zh-CN") == "batch-zh", "批量部分译文被丢弃");
        });
    }

    static void AggregatePartialTargets()
    {
        using var server = new LocalServer(r => Task.FromResult(PartialResponse(r)));
        var primary = Provider(server);
        var backup = Provider(server, path: "/backup");
        WithSettings([primary, backup], (s, engine) =>
        {
            s.MultiTargetEnabled = true;
            var received = new ConcurrentQueue<TranslateResult>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var task = engine.AggregateAsync(Text, timeout.Token, onResult: received.Enqueue);
            PumpUntil(task);
            Check(received.Count == 2 && task.Result.Results.Count == 2, "聚合没有完整回调各源");
            Check(!received.Single(r => r.ProviderId == primary.Id).Ok, "聚合回调把部分失败当成成功");
            Check(received.Single(r => r.ProviderId == backup.Id).Ok, "聚合误伤了全部成功的源");
        });
    }

    static void PartialRecovery()
    {
        var frenchCalls = 0;
        using var server = new LocalServer(r => Task.FromResult(
            r.Json["target"]?.ToString() == "fr" && Interlocked.Increment(ref frenchCalls) == 1
                ? Json(new { error = "temporary failure" })
                : Json(new { translatedText = "recovered-" + r.Json["target"] })));
        var cfg = Provider(server);
        WithSettings([cfg], (s, engine) =>
        {
            s.MultiTargetEnabled = true;
            Check(!Single(engine, cfg).Results.Single().Ok, "首轮失败未生效");
            var recovered = Single(engine, cfg).Results.Single();
            Check(recovered.Ok && recovered.Texts.Count == 2 && recovered.Error is null, "补齐目标后仍被标成失败");
            Check(engine.Health.LastOk(cfg.Id) && server.Requests.Count == 3, "恢复后未重置健康状态或重复请求成功目标");
            Check(Single(engine, cfg).Results.Single().FromCache && server.Requests.Count == 3, "完整恢复结果没有命中缓存");
        });
    }

    static void BlankTranslation()
    {
        using var server = new LocalServer(_ => Task.FromResult(Json(new { translatedText = " \r\n " })));
        var cfg = Provider(server);
        WithSettings([cfg], (_, engine) =>
        {
            var first = Single(engine, cfg).Results.Single();
            Check(!first.Ok && first.Texts.Count == 0 && first.Error is not null, "空白译文被当成成功");
            Check(!Single(engine, cfg).Results.Single().FromCache && server.Requests.Count == 2, "空白译文进入了缓存");
        });
    }

    static void CachedPartialTarget()
    {
        using var server = new LocalServer(r => Task.FromResult(PartialResponse(r)));
        var cfg = Provider(server);
        WithSettings([cfg], (s, engine) =>
        {
            Check(Single(engine, cfg).Results.Single().Ok, "准备目标缓存失败");
            s.MultiTargetEnabled = true;
            SettingsService.Instance.Touch();
            var result = Single(engine, cfg).Results.Single();
            Check(!result.Ok && !result.FromCache && result.Get("zh-CN") is not null, "已有缓存掩盖了新目标的失败");
            Check(server.Requests.Count == 2, "更改目标语言不应清掉已有语向缓存");
        });
    }

    static void ConfigurationChanges()
    {
        using var server = new LocalServer(r => Task.FromResult(Ai("reply-" + r.Json["model"] + "-" + r.Path)));
        var cfg = Provider(server, ProviderKind.OpenAiCompat);
        WithSettings([cfg], (_, engine) =>
        {
            Check(Single(engine, cfg).Results.Single().Ok, "缓存准备失败");
            Check(Single(engine, cfg).Results.Single().FromCache && server.Requests.Count == 1, "未修改配置就没有命中缓存");
            foreach (var (key, value) in new[]
            {
                ("model", "new-model"), ("prompt", "Only translate to {target}."),
                ("baseUrl", server.Url + "/changed"), ("apiKey", "local-test-key-b"),
            })
            {
                var count = server.Requests.Count;
                cfg.Options[key] = value;
                SettingsService.Instance.Touch();
                var result = Single(engine, cfg).Results.Single();
                Check(result.Ok && !result.FromCache && server.Requests.Count == count + 1, key + " 变更后仍命中旧配置缓存");
                Check(Single(engine, cfg).Results.Single().FromCache, key + " 变更后新译文不能缓存");
            }
        });
    }

    static void UnrelatedSettings()
    {
        using var server = new LocalServer(_ => Task.FromResult(Json(new { translatedText = "stable" })));
        var cfg = Provider(server);
        WithSettings([cfg], (s, engine) =>
        {
            _ = Single(engine, cfg);
            s.AlwaysOnTop = !s.AlwaysOnTop;
            SettingsService.Instance.Touch();
            Check(Single(engine, cfg).Results.Single().FromCache, "仅改置顶意外清空翻译缓存");
            cfg.Options = cfg.Options.Reverse().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            SettingsService.Instance.Touch();
            Check(Single(engine, cfg).Results.Single().FromCache && server.Requests.Count == 1, "选项顺序变化意外清空缓存");
        });
    }

    static void ConfigurationSeparators()
    {
        using var server = new LocalServer(r => Task.FromResult(Ai(r.Json["model"]!.ToString())));
        var cfg = Provider(server, ProviderKind.OpenAiCompat);
        cfg.Options["model"] = "model;prompt=first";
        cfg.Options["prompt"] = "second";
        WithSettings([cfg], (_, engine) =>
        {
            Single(engine, cfg);
            cfg.Options["model"] = "model";
            cfg.Options["prompt"] = "first;prompt=second";
            SettingsService.Instance.Touch();
            var result = Single(engine, cfg).Results.Single();
            Check(result.Ok && !result.FromCache && result.Get("zh-CN") == "model", "分隔符导致配置签名碰撞并返回旧缓存");
        });
    }

    static void InflightConfigurationChange()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new LocalServer(async r =>
        {
            var model = r.Json["model"]!.ToString();
            if (model == "local-test-model")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(r.Token);
            }
            return Ai(model);
        });
        var cfg = Provider(server, ProviderKind.OpenAiCompat);
        WithSettings([cfg], (_, engine) =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pending = engine.SingleAsync(cfg.Id, Text, false, timeout.Token);
            try
            {
                PumpUntil(entered.Task);
                cfg.Options["model"] = "new-model";
                SettingsService.Instance.Touch();
            }
            finally { release.TrySetResult(); }
            PumpUntil(pending);
            var result = Single(engine, cfg).Results.Single();
            Check(result.Ok && !result.FromCache && result.Get("zh-CN") == "new-model", "在途旧请求污染了新配置缓存");
            Check(Single(engine, cfg).Results.Single().FromCache, "新配置结果未正常缓存");
        });
    }

    static void StreamCase(string mode)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new LocalServer(r =>
        {
            if (r.Path.StartsWith("/backup/", StringComparison.Ordinal))
                return Task.FromResult(Json(new { translatedText = "backup-result" }));
            if (r.Json["stream"]?.GetValue<bool>() != true)
                return Task.FromResult(mode == "retry" ? Ai("retry-result") : Json(new { error = new { message = "local retry failure" } }));
            if (mode == "cancel") cancel.Cancel();
            var sse = mode switch
            {
                "choices" => "data: {\"choices\":[]}\n\n",
                "reasoning" => "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"thinking\"}}]}\n\n",
                "cleanup" => "data: {\"choices\":[{\"delta\":{\"content\":\"Translation: \"}}]}\n\n",
                "success" => "data: {\"choices\":[{\"delta\":{\"content\":\"stream-result\"}}]}\n\n",
                _ => "",
            };
            return Task.FromResult(mode == "error" ? new Response("local stream failure", Status: 500)
                : new Response(sse + "data: [DONE]\n\n", "text/event-stream"));
        });
        var primary = Provider(server, ProviderKind.OpenAiCompat);
        var backup = Provider(server, path: "/backup");
        WithSettings([primary, backup], (s, engine) =>
        {
            s.AutoFallback = mode != "disabled";
            var window = new MainWindow(new AppHost());
            try
            {
                if (mode == "disabled")
                    typeof(MainWindow).GetField("_batch", PrivateInstance)!.SetValue(window,
                        new TranslateBatch
                        {
                            SourceText = "previous input", Targets = ["zh-CN"],
                            Results = [new TranslateResult { Texts = new() { ["zh-CN"] = "previous translation" } }],
                        });
                window.SetInput(Text, translate: false);
                var task = (Task)typeof(MainWindow).GetMethod("RunTranslateAsync", PrivateInstance)!
                    .Invoke(window, [Text, cancel, true])!;
                PumpUntil(task);
                var batch = (TranslateBatch?)typeof(MainWindow).GetField("_batch", PrivateInstance)!.GetValue(window);
                var ordinary = server.Requests.Count(r => r.Path.StartsWith("/primary/", StringComparison.Ordinal) && r.Json["stream"]?.GetValue<bool>() == false);
                var backups = server.Requests.Count(r => r.Path.StartsWith("/backup/", StringComparison.Ordinal));
                if (mode is "disabled" or "cancel")
                {
                    Check(ordinary == 0 && backups == 0, "关闭降级或取消请求后仍发起了重试");
                    if (mode == "cancel")
                    {
                        Check(batch is null && engine.Health.LastOk(primary.Id), "已取消流污染了结果或源健康状态");
                    }
                    else
                    {
                        Check(!engine.Health.LastOk(primary.Id), "空流没有记为失败");
                        Check(batch?.SourceText == Text && !batch.Results.Any(r => r.Ok) && ResultView.AllText(batch).Length == 0,
                            "空流失败后仍保留可复制的上一条译文");
                        Check(!((TextBlock)window.FindName("StatusText")).Text.Contains("正在输出"), "空流结束后状态栏仍在等待");
                        var view = (ResultView)typeof(MainWindow).GetField("_result", PrivateInstance)!.GetValue(window)!;
                        Check(Descendants(view).OfType<TextBlock>().Any(t => t.Text.Contains("模型未返回译文")), "关闭降级后没有空流错误提示");
                    }
                    return;
                }
                if (mode is "success" or "retry")
                {
                    Check(batch?.Results.LastOrDefault()?.Get("zh-CN") == (mode == "success" ? "stream-result" : "retry-result"), "成功译文丢失");
                    Check(backups == 0 && ordinary == (mode == "success" ? 0 : 1), "成功流或普通重试成功后仍继续换源");
                    return;
                }
                Check(batch?.Results.LastOrDefault(r => r.Ok)?.ProviderId == backup.Id && ordinary == 1 && backups == 1, "流式失败没有经过普通重试和备用源");
                Check((string?)typeof(MainWindow).GetField("_activeProviderId", PrivateInstance)!.GetValue(window) == backup.Id, "换源后标签没有同步");
                Check(((TextBlock)window.FindName("StatusText")).Text.StartsWith("backup", StringComparison.Ordinal), "换源后状态栏仍显示旧源");
            }
            finally { cancel.Cancel(); window.Close(); }
        });
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    static void PumpUntil(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("本机翻译回归超时");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
    }

    sealed record Request(string Path, JsonNode Json, CancellationToken Token);
    sealed record Response(string Body, string ContentType = "application/json", int Status = 200);

    sealed class LocalServer : IDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        readonly List<Task> _clients = [];
        readonly Task _listen;
        readonly Func<Request, Task<Response>> _respond;
        public ConcurrentQueue<Request> Requests { get; } = new();
        public string Url { get; }

        public LocalServer(Func<Request, Task<Response>> respond)
        {
            _respond = respond;
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _listen = Task.Run(Listen);
        }

        async Task Listen()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                    _clients.Add(Serve(await _listener.AcceptTcpClientAsync(_stop.Token)));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var data = new MemoryStream();
                    var buffer = new byte[4096];
                    int headerEnd;
                    string headers;
                    while (true)
                    {
                        var count = await stream.ReadAsync(buffer, _stop.Token);
                        if (count == 0) throw new IOException("本机测试请求提前结束");
                        data.Write(buffer, 0, count);
                        headers = Encoding.UTF8.GetString(data.ToArray());
                        headerEnd = headers.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (headerEnd >= 0) break;
                        if (data.Length > 16384) throw new IOException("本机测试请求头超限");
                    }
                    var length = int.Parse(headers[..headerEnd].Split("\r\n")
                        .Single(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                    Check(length is >= 0 and <= 1048576, "本机测试请求体超限");
                    while (data.Length < headerEnd + 4 + length)
                    {
                        var count = await stream.ReadAsync(buffer, _stop.Token);
                        if (count == 0) throw new IOException("本机测试请求体提前结束");
                        data.Write(buffer, 0, count);
                    }
                    var request = new Request(headers.Split(' ')[1], JsonNode.Parse(data.ToArray().AsSpan(headerEnd + 4, length))!, _stop.Token);
                    Requests.Enqueue(request);
                    var response = await _respond(request);
                    var body = Encoding.UTF8.GetBytes(response.Body);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} Test\r\nContent-Type: {response.ContentType}; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, _stop.Token);
                    await stream.WriteAsync(body, _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) { /* 客户端取消时断开连接，不影响后续隔离用例。 */ }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listen.GetAwaiter().GetResult();
            _listener.Stop();
            Task.WhenAll(_clients).GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
