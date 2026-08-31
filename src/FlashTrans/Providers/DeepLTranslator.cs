using System.Text.Json.Nodes;
using FlashTrans.Core;

namespace FlashTrans.Providers;

/// <summary>DeepL 官方接口。Key 以 :fx 结尾自动走 Free 版地址。</summary>
public sealed class DeepLTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var key = Opt("apiKey");
        var to = LangCodes.DeepL(req.SingleTarget, target: true) ?? throw Unsupported(req.SingleTarget);
        var from = LangCodes.DeepL(req.From, target: false);

        var host = Opt("endpoint");
        if (string.IsNullOrWhiteSpace(host))
            host = key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
                ? "https://api-free.deepl.com" : "https://api.deepl.com";
        var url = host.TrimEnd('/');
        if (!url.Contains("/v2/translate")) url += "/v2/translate";

        var payload = new JsonObject
        {
            ["text"] = new JsonArray(req.Text),
            ["target_lang"] = to,
        };
        if (from is not null) payload["source_lang"] = from;

        var body = await Net.PostJsonAsync(url, payload.ToJsonString(), TimeoutMs, ct,
            r => r.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key));

        var json = Net.Json(body);
        if (json["message"] is { } msg && json["translations"] is null)
            throw new ProviderException(msg.ToString());

        var arr = json["translations"] as JsonArray ?? throw new ProviderException("返回格式异常");
        var text = arr.Count > 0 ? arr[0]?["text"]?.GetValue<string>() : null;
        if (string.IsNullOrEmpty(text)) throw new ProviderException("接口未返回译文");

        var res = New();
        res.Texts[req.SingleTarget] = text;
        res.DetectedFrom = arr[0]?["detected_source_language"]?.GetValue<string>()?.ToLowerInvariant();
        return res;
    }
}

/// <summary>DeepLX：自建的 DeepL 免费代理。</summary>
public sealed class DeepLXTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var url = Opt("endpoint", "http://127.0.0.1:1188/translate").TrimEnd('/');
        var to = LangCodes.DeepL(req.SingleTarget, target: false) ?? throw Unsupported(req.SingleTarget);
        var from = LangCodes.DeepL(req.From, target: false) ?? "auto";

        var payload = new JsonObject
        {
            ["text"] = req.Text,
            ["source_lang"] = from,
            ["target_lang"] = to,
        };

        var token = Opt("token");
        var body = await Net.PostJsonAsync(url, payload.ToJsonString(), TimeoutMs, ct, r =>
        {
            if (!string.IsNullOrWhiteSpace(token)) Net.Bearer(r, token);
        });

        var json = Net.Json(body);
        var code = json["code"]?.GetValue<int>() ?? 200;
        if (code != 200)
            throw new ProviderException($"DeepLX 返回 {code} {json["message"]?.ToString() ?? ""}".Trim());

        var text = json["data"]?.GetValue<string>();
        if (string.IsNullOrEmpty(text)) throw new ProviderException("接口未返回译文");

        var res = New();
        res.Texts[req.SingleTarget] = text;
        res.DetectedFrom = json["source_lang"]?.ToString()?.ToLowerInvariant();
        return res;
    }
}
