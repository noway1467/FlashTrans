using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlashTrans.Core;

/// <summary>共享 HttpClient（连接池复用 + 预热），避免每次翻译重新握手。</summary>
public static partial class Net
{
    const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                      "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36";

    static volatile HttpClient _client = Build(null);
    static string? _proxy;
    static readonly object ConfigGate = new();

    public static HttpClient Client => _client;

    static HttpClient Build(string? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            // 预热建立的连接要能活到用户真正开始翻译，别几分钟就自己断掉重新握手
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            EnableMultipleHttp2Connections = true,
        };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }
        var c = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        // 注意：这里不设 DefaultRequestVersion。它只对 HttpClient 自己造的请求（GetAsync(url) 之类）
        // 有效，而本程序全是自己 new HttpRequestMessage —— 那种请求自带 1.1/OrLower，
        // 客户端的默认值压根不会被查。版本改在 PreferHttp2 里逐请求设。
        c.DefaultRequestHeaders.UserAgent.ParseAdd(Ua);
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,zh-CN;q=0.8");
        return c;
    }

    /// <summary>
    /// 代理变更时重建客户端。代理没变就原样留着——每次「确定」都重建会把
    /// 预热好的 TLS 连接全丢掉，下一次翻译又要从握手开始；
    /// 而且旧客户端上可能还有在途请求，立刻 Dispose 会让它们抛 ObjectDisposedException。
    /// </summary>
    public static void Configure(string? proxy)
    {
        var next = string.IsNullOrWhiteSpace(proxy) ? null : proxy!.Trim();

        lock (ConfigGate)
        {
            if (string.Equals(_proxy, next, StringComparison.Ordinal)) return;
            _proxy = next;
            var old = _client;
            _client = Build(next);
            // 在途请求还持有 old，等它们收尾后再回收连接池
            _ = Task.Delay(TimeSpan.FromSeconds(35)).ContinueWith(_ =>
            {
                try { old.Dispose(); } catch { /* ignore */ }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 请求 HTTP/2。支持的源（谷歌、必应、DeepL 等）能少几个往返，聚合时多个请求还能复用同一条连接。
    /// 用 OrLower：ALPN 里同时报 h2 和 http/1.1 让服务器挑，谈不成就退回 1.1；
    /// 明文 http:// 不会去试 h2c，自建源照旧走 1.1。
    /// 预热和真实请求必须用同一个版本，否则建立的连接对不上、复用不了。
    /// </summary>
    public static void PreferHttp2(HttpRequestMessage req)
    {
        req.Version = System.Net.HttpVersion.Version20;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    }

    /// <summary>后台预热 DNS/TLS 连接，首次翻译可省下几百毫秒。</summary>
    public static void Warmup(IEnumerable<string> urls)
    {
        // 固定用当前这个 client：期间若换了代理，预热到旧连接池上就没意义了
        var client = _client;
        foreach (var url in urls)
        {
            var target = url;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Head, target);
                    PreferHttp2(req);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    using var _ = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                              .ConfigureAwait(false);
                }
                catch { /* 预热失败无所谓，连接建起来了就算赚到 */ }
            });
        }
    }

    public static async Task<string> SendStringAsync(HttpRequestMessage req, int timeoutMs, CancellationToken ct)
    {
        PreferHttp2(req);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        HttpResponseMessage resp;
        try
        {
            resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                               .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException($"请求超时（>{timeoutMs}ms）");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("网络错误：" + Short(ex.Message));
        }

        using (resp)
        {
            string body;
            try
            {
                // 用 cts.Token：正文也该受这个源的超时约束，不该退回到 HttpClient 的 30s
                body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ProviderException($"请求超时（>{timeoutMs}ms）");
            }
            if (!resp.IsSuccessStatusCode) throw new ProviderException(Describe(resp, body));
            return body;
        }
    }

    /// <summary>把失败响应变成一句人能看懂的话：免费接口常常直接回一整页 HTML。</summary>
    static string Describe(HttpResponseMessage resp, string body)
    {
        var code = (int)resp.StatusCode;
        var isHtml = body.TrimStart().StartsWith('<');

        // 限流、密钥这类状态码本身就说明了问题，优先用它。
        var hint = code switch
        {
            429 => "请求过于频繁，被限流了，过一会儿再试或换个源",
            401 => "密钥无效或未授权",
            403 => "密钥没有权限，或该接口在当前地区不可用",
            456 => "额度已用完",
            >= 500 when !isHtml => "对方服务异常",
            _ => null,
        };
        if (hint is not null) return $"HTTP {code} · {hint}";

        // 回的是整页 HTML：地址指向的是网页而不是接口，这比状态码有用得多。
        if (isHtml) return $"HTTP {code} · {NotJson(body)}";

        if (code is 404 or 405) return $"HTTP {code} · 接口地址不对";
        if (code >= 500) return $"HTTP {code} · 对方服务异常";

        var detail = Short(Clean(body), 100);
        return detail.Length == 0 ? $"HTTP {code} {resp.ReasonPhrase}" : $"HTTP {code} · {detail}";
    }

    public static Task<string> GetStringAsync(string url, int timeoutMs, CancellationToken ct,
                                              Action<HttpRequestMessage>? setup = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        setup?.Invoke(req);
        return SendStringAsync(req, timeoutMs, ct);
    }

    public static Task<string> PostJsonAsync(string url, string json, int timeoutMs, CancellationToken ct,
                                             Action<HttpRequestMessage>? setup = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        setup?.Invoke(req);
        return SendStringAsync(req, timeoutMs, ct);
    }

    public static Task<string> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form,
                                             int timeoutMs, CancellationToken ct,
                                             Action<HttpRequestMessage>? setup = null)
    {
        var body = string.Join("&", form.Select(kv =>
            Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        setup?.Invoke(req);
        return SendStringAsync(req, timeoutMs, ct);
    }

    public static JsonNode Json(string body)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body, documentOptions: new() { AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            // 状态码是 200 但内容不是 JSON：Describe 管不到这里，单独给句人话。
            throw new ProviderException(NotJson(body));
        }
        return node ?? throw new ProviderException("返回内容为空");
    }

    /// <summary>返回体不是 JSON 时，给一句能照着改的话——绝大多数是接口地址填成了网页。</summary>
    public static string NotJson(string body)
    {
        var head = body.TrimStart();
        if (head.Length == 0) return "接口返回了空内容";
        if (!head.StartsWith('<')) return "接口返回的不是 JSON：" + Short(Clean(head), 100);

        var title = Clean(WebUtility.HtmlDecode(HtmlTitle().Match(head).Groups[1].Value));
        var where = title.Length > 0 ? $"（页面标题：{title}）" : "";
        return $"接口返回的是网页而不是 JSON{where}。请检查「接口地址」：" +
               "OpenAI 兼容接口要填 API 地址且通常以 /v1 结尾，不是网页版聊天或文档的网址。";
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlTitle();

    public static void Bearer(HttpRequestMessage req, string key)
        => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

    static string Clean(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    static string Short(string s, int max = 140) => s.Length <= max ? s : s[..max] + "…";
}
