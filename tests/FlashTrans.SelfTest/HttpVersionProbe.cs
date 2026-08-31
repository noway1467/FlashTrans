using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using FlashTrans.Core;

namespace FlashTrans.SelfTest;

/// <summary>
/// Net.Client 默认请求 HTTP/2。明文 http:// 没有 ALPN 可谈，自建源（DeepLX、LibreTranslate）
/// 又常是明文 + 只会 HTTP/1.1，所以要确认它不会去试 h2c 把请求打死。
/// </summary>
static class HttpVersionProbe
{
    public static void RunAll(Action<string, Action> step)
    {
        step("HTTP：明文自建源仍走 HTTP/1.1", PlaintextFallsBackTo11);
        step("HTTP：版本设在请求上而不是客户端默认值上", VersionIsSetPerRequest);
    }

    /// <summary>
    /// 坑在这里：HttpClient.DefaultRequestVersion 只对 HttpClient 自己造的请求
    /// （GetAsync(url) 那种）有效。自己 new HttpRequestMessage 时它自带 1.1/OrLower，
    /// 客户端的默认值压根不会被查——设了也白设。本程序全是自己 new，所以必须逐请求设。
    /// </summary>
    static void VersionIsSetPerRequest()
    {
        using var raw = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        Console.WriteLine($"       new 出来就是 {raw.Version}/{raw.VersionPolicy}");
        if (raw.Version != System.Net.HttpVersion.Version11)
            throw new InvalidOperationException("HttpRequestMessage 的默认版本变了，本注释要重写");

        using var prepared = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        Net.PreferHttp2(prepared);
        Console.WriteLine($"       PreferHttp2 之后 {prepared.Version}/{prepared.VersionPolicy}");
        if (prepared.Version != System.Net.HttpVersion.Version20)
            throw new InvalidOperationException("PreferHttp2 没把版本设成 2.0，h2 会谈不成");
        if (prepared.VersionPolicy != HttpVersionPolicy.RequestVersionOrLower)
            throw new InvalidOperationException(
                "策略不是 OrLower，明文自建源可能被强行升级到 h2c 而连不上：" + prepared.VersionPolicy);
    }

    static void PlaintextFallsBackTo11()
    {
        // 只会 HTTP/1.1 的极简监听器：把收到的请求行回显出来
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string requestLine = "";
        var served = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buf = new byte[4096];
            var n = await stream.ReadAsync(buf);
            requestLine = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0];

            var body = "{\"ok\":true}";
            var resp = "HTTP/1.1 200 OK\r\n" +
                       "Content-Type: application/json\r\n" +
                       $"Content-Length: {body.Length}\r\n" +
                       "Connection: close\r\n\r\n" + body;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(resp));
            await stream.FlushAsync();
        });

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/translate");
            Net.PreferHttp2(req);   // 和真实请求走同一条路：Net.SendStringAsync 就是这么设的
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // 必须走 SendAsync：同步的 Send 在 .NET 里不支持 HTTP/2，会无声退回 1.1，
            // 那样测出来的「退回 1.1」是假的，什么都没证明。
            using var resp = Net.Client.SendAsync(req, cts.Token).GetAwaiter().GetResult();
            var text = resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();

            served.GetAwaiter().GetResult();
            Console.WriteLine($"       请求行: {requestLine}  响应版本: {resp.Version}");

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException("明文 HTTP/1.1 源请求失败：" + (int)resp.StatusCode);
            if (text != "{\"ok\":true}")
                throw new InvalidOperationException("正文不对：" + text);
            if (!requestLine.EndsWith("HTTP/1.1", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "客户端没退回 HTTP/1.1，自建明文源会连不上：" + requestLine);
        }
        finally
        {
            listener.Stop();
        }
    }
}
