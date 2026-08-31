using FlashTrans.Core;
using FlashTrans.Services;

namespace FlashTrans.SelfTest;

/// <summary>真实网络探测：只在传 --net 时跑，用免费源验证端到端链路。</summary>
static class NetProbe
{
    public static void Run(Action<string, Action> step)
    {
        Console.WriteLine("\n[联网测试]");
        var engine = TranslateEngine.Instance;
        var s = SettingsService.Instance.Current;

        foreach (var cfg in s.Providers.Where(p => p.Enabled).ToList())
        {
            var c = cfg;
            step($"{c.DisplayName}：TestAsync", () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var r = engine.TestAsync(c, cts.Token).GetAwaiter().GetResult();
                var text = r.Texts.Values.FirstOrDefault() ?? "";
                Console.WriteLine($"       {(r.Ok ? "译文" : "错误")}: " +
                                  (r.Ok ? Shorten(text) : r.Error) + $"  ({r.ElapsedMs}ms)");
                if (r.Ok) return;

                // 限流/额度是环境问题，不算代码缺陷：本机 IP 被免费接口挡住时很常见
                if (Environmental(r.Error))
                {
                    Console.WriteLine("       （环境限制，跳过）");
                    return;
                }
                throw new InvalidOperationException(r.Error ?? "失败");
            });
        }

        step("腾讯交互翻译：英译中 + 繁体是真繁体 + 不支持的语言有话说", TranSmartProbe);

        step("聚合翻译：英译中", () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var batch = engine.AggregateAsync("Hello, world. How are you today?", cts.Token)
                              .GetAwaiter().GetResult();
            foreach (var r in batch.Results)
                Console.WriteLine($"       {r.ProviderName}: " +
                                  (r.Ok ? Shorten(r.Get("zh-CN") ?? "") : "失败 " + r.Error));
            if (!batch.Results.Any(r => r.Ok)) throw new InvalidOperationException("所有源都失败了");
        });

        step("中译英自动互译", () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var (from, targets) = engine.Resolve("今天天气不错");
            Console.WriteLine($"       解析：{from} -> {string.Join(",", targets)}");
            var id = s.Providers.First(p => p.Enabled).Id;
            var batch = engine.SingleAsync(id, "今天天气不错", false, cts.Token).GetAwaiter().GetResult();
            var r = batch.Results.LastOrDefault(x => x.Ok)
                    ?? throw new InvalidOperationException(batch.Results.LastOrDefault()?.Error ?? "无结果");
            Console.WriteLine($"       {r.ProviderName}: {Shorten(r.Texts.Values.First())}");
        });

        step("缓存命中", () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var id = s.Providers.First(p => p.Enabled).Id;
            engine.SingleAsync(id, "cache probe sentence", false, cts.Token).GetAwaiter().GetResult();
            var second = engine.SingleAsync(id, "cache probe sentence", false, cts.Token)
                               .GetAwaiter().GetResult();
            var hit = second.Results.Any(r => r.FromCache);
            Console.WriteLine($"       第二次命中缓存：{hit}");
            if (!hit) throw new InvalidOperationException("缓存没生效");
        });

        step("失败降级链", () =>
        {
            var broken = ProviderConfig.Create(ProviderKind.DeepLX);
            broken.Options["endpoint"] = "http://127.0.0.1:9/translate";   // 必然连不上
            broken.TimeoutMs = 1500;
            s.Providers.Insert(0, broken);
            engine.Registry.Invalidate();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var batch = engine.SingleAsync(broken.Id, "fallback probe", false, cts.Token)
                                  .GetAwaiter().GetResult();
                Console.WriteLine($"       备注：{string.Join(" / ", batch.Notes)}");
                if (!batch.Results.Any(r => r.Ok)) throw new InvalidOperationException("降级没能救回来");
            }
            finally
            {
                s.Providers.Remove(broken);
                engine.Health.Reset(broken.Id);
                engine.Registry.Invalidate();
            }
        });

        step("接口地址填成网页：错误信息可读", () =>
        {
            // 用户把「接口地址」填成网页时，服务器回 200 + HTML，
            // 以前会漏出 JsonReaderException: '<' is an invalid start of a value。
            var ai = ProviderConfig.Create(ProviderKind.OpenAiCompat);
            ai.Options["baseUrl"] = "https://example.com";
            ai.Options["model"] = "gpt-4o-mini";
            ai.Options["apiKey"] = "probe-not-a-real-key";
            ai.Options["stream"] = "false";
            ai.TimeoutMs = 15000;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var r = TranslateEngine.Instance.TestAsync(ai, cts.Token).GetAwaiter().GetResult();
            Console.WriteLine($"       错误: {r.Error}");

            if (r.Ok) throw new InvalidOperationException("example.com 不该翻译成功");
            if (Environmental(r.Error)) { Console.WriteLine("       （环境限制，跳过）"); return; }
            if (r.Error is null || r.Error.Contains("JsonReaderException")
                               || r.Error.Contains("invalid start of a value"))
                throw new InvalidOperationException("原始 Json 异常又漏出来了");
            if (!r.Error.Contains("网页") && !r.Error.Contains("不是 JSON"))
                throw new InvalidOperationException("错误信息没说清是地址问题：" + r.Error);
        });

        step("HTTPS 源协商到 HTTP/2", () =>
        {
            // 每个源用全新客户端，避开连接池里已有的 1.1 连接（ALPN 只在建连时谈一次）
            var negotiated = new List<string>();
            var reachable = false;
            foreach (var url in (string[])["https://www.bing.com/translator", "https://translate.googleapis.com/"])
            {
                var host = new Uri(url).Host;
                try
                {
                    using var req = new System.Net.Http.HttpRequestMessage(
                        System.Net.Http.HttpMethod.Head, url);
                    Net.PreferHttp2(req);   // 和真实请求同一条路
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    // 同步 Send 不支持 h2，会无声退回 1.1；必须走 SendAsync
                    using var resp = Net.Client.SendAsync(req, cts.Token).GetAwaiter().GetResult();
                    reachable = true;
                    negotiated.Add($"{host} → HTTP/{resp.Version}");
                }
                catch (Exception ex) { negotiated.Add($"{host} → 连不上（{ex.GetType().Name}）"); }
            }
            Console.WriteLine("       " + string.Join("；", negotiated));

            if (!reachable) { Console.WriteLine("       （都没连上，按环境限制跳过）"); return; }
            if (!negotiated.Any(n => n.Contains("HTTP/2")))
                throw new InvalidOperationException("HTTPS 源一个都没谈成 h2：" + string.Join("；", negotiated));
        });

        step("接口地址缺协议头：提交前就拦住", () =>
        {
            var ai = ProviderConfig.Create(ProviderKind.OpenAiCompat);
            ai.Options["baseUrl"] = "api.deepseek.com/v1";   // 少了 https://
            ai.Options["model"] = "deepseek-chat";
            var err = TranslateEngine.Instance.ConfigErrorOf(ai);
            Console.WriteLine($"       提示: {err}");
            if (err is null || !err.Contains("http"))
                throw new InvalidOperationException("没拦住缺协议头的地址");
        });
    }

    /// <summary>
    /// 自己造配置，不依赖 settings.json 里有没有这个源。
    /// 重点验三件事：能出译文；zh-TW 真的是繁体（彩云那种假繁体就是这么发现的）；
    /// 不支持的语言要在本地被 LangCodes 拦住，给出「该源不支持…」。
    /// </summary>
    static void TranSmartProbe()
    {
        var cfg = ProviderConfig.Create(ProviderKind.TranSmart);
        cfg.TimeoutMs = 15000;
        var impl = TranslateEngine.Instance.Registry.Get(cfg);

        string Run(string text, string to, string from = Languages.Auto)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var r = impl.TranslateAsync(new TranslateRequest { Text = text, From = from, Targets = [to] },
                                        cts.Token).GetAwaiter().GetResult();
            if (!r.Ok) throw new InvalidOperationException(r.Error ?? "无结果");
            return r.Texts[to];
        }

        string zh;
        try
        {
            zh = Run("Machine learning and computer networks are complex.", "zh-CN");
        }
        catch (Exception ex) when (Environmental(ex.Message))
        {
            Console.WriteLine($"       {ex.Message}（环境限制，跳过）");
            return;
        }
        Console.WriteLine($"       zh-CN: {Shorten(zh)}");
        if (!zh.Any(c => c >= 0x4E00 && c <= 0x9FFF))
            throw new InvalidOperationException("英译中没返回中文：" + zh);

        var tw = Run("Machine learning and computer networks are complex.", "zh-TW");
        Console.WriteLine($"       zh-TW: {Shorten(tw)}");
        // 繁体专有字形。整段一模一样就说明它把简体原样回来了。
        if (tw == zh || !tw.Any(c => "機學計算網絡複雜體與".Contains(c)))
            throw new InvalidOperationException($"zh-TW 返回的不是繁体：{tw}");

        // 印尼语接口会回 Unsupported-Language，LangCodes 里没收它，应该在发请求前就被拦下
        var req = new TranslateRequest { Text = "hello", Targets = ["id"] };
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var bad = impl.TranslateAsync(req, cts2.Token).GetAwaiter().GetResult();
        Console.WriteLine($"       id: {bad.Error}");
        if (bad.Ok || bad.Error is null || !bad.Error.Contains("不支持"))
            throw new InvalidOperationException("不支持的语言没给出清楚提示：" + (bad.Error ?? "居然成功了"));
    }

    /// <summary>限流、额度、被墙这类环境问题，与代码无关。</summary>
    static bool Environmental(string? error) =>
        error is not null &&
        (error.Contains("429") || error.Contains("限流") || error.Contains("额度")
         || error.Contains("超时") || error.Contains("网络错误"));

    static string Shorten(string text)
    {
        text = text.Replace('\n', ' ').Trim();
        return text.Length <= 46 ? text : text[..46] + "…";
    }
}
