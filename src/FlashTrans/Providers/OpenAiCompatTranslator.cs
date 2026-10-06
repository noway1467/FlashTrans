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
            var url = BaseUrl;
            if (string.IsNullOrWhiteSpace(url)) return "请先填写「接口地址」";
            try { ApiEndpoint(url, "chat/completions"); }
            catch (ProviderException ex) { return ex.Message; }
            if (string.IsNullOrWhiteSpace(Opt("model"))) return "请先填写「模型」";
            return null;
        }
    }

    public bool StreamEnabled => Opt("stream", "true").Equals("true", StringComparison.OrdinalIgnoreCase);

    // 显式清空地址时必须报错，不能让 Opt 的默认值把 Key 发给另一家服务。
    string BaseUrl => Cfg.Options.GetValueOrDefault("baseUrl", "https://api.openai.com/v1");
    string Endpoint() => ApiEndpoint(BaseUrl, "chat/completions");

    internal static string ApiEndpoint(string baseUrl, string resource)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ProviderException("接口地址须为 http:// 或 https:// API 地址，不含用户信息、查询参数或锚点");

        var path = uri.AbsolutePath.TrimEnd('/');
        // 兼容用户直接粘贴聊天接口；保留网关前缀及 /v4 等自定义版本路径。
        foreach (var suffix in new[] { "/chat/completions", "/models" })
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length];
                break;
            }
        if (path.Length == 0) path = "/v1";
        return uri.GetLeftPart(UriPartial.Authority) + path + "/" + resource;
    }

    /// <summary>显式拉取当前凭据可见的模型；不要求先填模型，也不发送翻译正文。</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiEndpoint(BaseUrl, "models"));
        Auth(request);
        Net.PreferHttp2(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Clamp(TimeoutMs, 800, 60000));
        try
        {
            using var response = await Net.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 不展示服务返回的原始错误正文，网关可能在里面回显密钥。
                var hint = (int)response.StatusCode switch
                {
                    401 => "请填写有效的 API Key",
                    403 => "此 Key 没有模型列表权限，请更换凭据或手动填写模型",
                    404 or 405 => "地址不正确或服务不支持模型列表，请检查地址或手动填写模型",
                    429 => "请求过于频繁，请稍后重试",
                    _ => "服务未能返回模型列表，请稍后重试或手动填写模型",
                };
                throw new ProviderException($"HTTP {(int)response.StatusCode} · {hint}");
            }
            // 为异常网关响应设上限，避免误填下载地址时把大文件读进内存。
            await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new ProviderException("响应不是 OpenAI 兼容的模型列表，请检查接口地址或手动填写模型");
            return data.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("id").GetString()!.Trim())
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("拉取模型超时，请检查网络或增加该源的超时时间");
        }
        catch (JsonException)
        {
            throw new ProviderException("响应不是有效的模型列表 JSON，请检查接口地址或手动填写模型");
        }
        catch (HttpRequestException)
        {
            throw new ProviderException("无法读取模型列表，请检查网络、代理及接口地址（响应上限 2 MB）");
        }
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
        if (!string.IsNullOrWhiteSpace(key)) Net.Bearer(r, key.Trim());
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
