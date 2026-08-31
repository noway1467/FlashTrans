using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlashTrans.Core;

namespace FlashTrans.Providers;

/// <summary>OpenAI 兼容接口：OpenAI / DeepSeek / Kimi / GLM / Qwen / Groq / Ollama / LM Studio 等通用。</summary>
public sealed class OpenAiCompatTranslator(ProviderConfig cfg) : TranslatorBase(cfg), IStreamingTranslator
{
    public override string? ConfigError
    {
        get
        {
            var url = Opt("baseUrl");
            if (string.IsNullOrWhiteSpace(url)) return "请先填写「接口地址」";
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "「接口地址」要以 http:// 或 https:// 开头";
            if (string.IsNullOrWhiteSpace(Opt("model"))) return "请先填写「模型」";
            return null;
        }
    }

    public bool StreamEnabled => Opt("stream", "true").Equals("true", StringComparison.OrdinalIgnoreCase);

    string Endpoint()
    {
        var b = Opt("baseUrl", "https://api.openai.com/v1").TrimEnd('/');
        return b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? b : b + "/chat/completions";
    }

    JsonObject BuildPayload(TranslateRequest req, bool stream)
    {
        var payload = new JsonObject
        {
            ["model"] = Opt("model", "gpt-4o-mini"),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = AiPrompt.Build(Opt("prompt"), req) },
                new JsonObject { ["role"] = "user", ["content"] = req.Text }),
            ["stream"] = stream,
        };
        if (double.TryParse(Opt("temperature", "0.2"), out var temp)) payload["temperature"] = temp;
        return payload;
    }

    void Auth(HttpRequestMessage r)
    {
        var key = Opt("apiKey");
        if (!string.IsNullOrWhiteSpace(key)) Net.Bearer(r, key);
    }

    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var body = await Net.PostJsonAsync(Endpoint(), BuildPayload(req, false).ToJsonString(), TimeoutMs, ct, Auth);
        var json = Net.Json(body);
        if (json["error"] is { } err)
            throw new ProviderException(err["message"]?.ToString() ?? err.ToString());

        var text = (json["choices"] as JsonArray)?.FirstOrDefault()?["message"]?["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)) throw new ProviderException("模型未返回译文");

        var res = New();
        res.Texts[req.SingleTarget] = AiPrompt.Cleanup(text);
        return res;
    }

    public async IAsyncEnumerable<string> StreamAsync(TranslateRequest req,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, Endpoint())
        {
            Content = new StringContent(BuildPayload(req, true).ToJsonString(),
                System.Text.Encoding.UTF8, "application/json")
        };
        Auth(httpReq);
        Net.PreferHttp2(httpReq);   // 流式请求走 SendAsync，不经 Net.SendStringAsync，得自己设

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Max(TimeoutMs, 20000));

        HttpResponseMessage resp;
        try
        {
            resp = await Net.Client.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException($"请求超时（>{TimeoutMs}ms）");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                throw new ProviderException($"HTTP {(int)resp.StatusCode} " + Trim(errBody));
            }

            // 200 但不是 SSE：地址填成网页时这里会一行 data: 都读不到，只剩空白。
            var mime = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (mime.Contains("html", StringComparison.OrdinalIgnoreCase))
                throw new ProviderException(Net.NotJson(await resp.Content.ReadAsStringAsync(ct)));

            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data.Length == 0 || data == "[DONE]") { if (data == "[DONE]") break; continue; }

                string? piece = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String)
                        piece = content.GetString();
                }
                catch (JsonException) { continue; }

                if (!string.IsNullOrEmpty(piece)) yield return piece;
            }
        }
    }

    static string Trim(string s) => s.Length <= 160 ? s.Replace('\n', ' ') : s[..160].Replace('\n', ' ') + "…";
}

public static class AiPrompt
{
    public static string Build(string? template, TranslateRequest req)
    {
        var t = string.IsNullOrWhiteSpace(template) ? ProviderMeta.DefaultPrompt : template!;
        var s = t.Replace("{target}", LangCodes.AiName(req.SingleTarget))
                 .Replace("{source}", LangCodes.AiName(req.From));
        if (!string.IsNullOrWhiteSpace(req.Style)) s += " " + req.Style;
        if (req.WantDictionary && LangDetect.LooksLikeWord(req.Text))
            s += " If the input is a single word or short phrase, give the main translation on the first line, " +
                 "then up to 3 alternative senses on following lines prefixed with '· '.";
        return s;
    }

    /// <summary>去掉模型偶尔加的引号、前缀。</summary>
    public static string Cleanup(string text)
    {
        var t = text.Trim();
        if (t.Length > 1 && ((t[0] == '"' && t[^1] == '"') || (t[0] == '“' && t[^1] == '”')))
            t = t[1..^1].Trim();
        foreach (var prefix in (string[])["译文：", "翻译：", "Translation:", "翻译结果："])
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                t = t[prefix.Length..].TrimStart();
        return t;
    }
}
