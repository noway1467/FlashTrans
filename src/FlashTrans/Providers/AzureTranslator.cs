using System.Text.Json.Nodes;
using FlashTrans.Core;

namespace FlashTrans.Providers;

/// <summary>微软 Azure 翻译（F0 免费层）。一次请求可返回多个目标语言。</summary>
public sealed class AzureTranslatorProvider(ProviderConfig cfg) : TranslatorBase(cfg)
{
    public override bool BatchTargets => true;

    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var host = Opt("endpoint", "https://api.cognitive.microsofttranslator.com").TrimEnd('/');
        var url = host + "/translate?api-version=3.0";

        // 目标语言 -> Azure 代码（保留双向映射用于回填）
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in req.Targets)
        {
            var code = LangCodes.Bing(t);
            if (string.IsNullOrEmpty(code)) continue;
            map[code] = t;
            url += "&to=" + Uri.EscapeDataString(code);
        }
        if (map.Count == 0) throw Unsupported(req.SingleTarget);

        var from = LangCodes.Bing(req.From);
        if (!string.IsNullOrEmpty(from)) url += "&from=" + Uri.EscapeDataString(from);

        var payload = new JsonArray(new JsonObject { ["Text"] = req.Text });
        var body = await Net.PostJsonAsync(url, payload.ToJsonString(), TimeoutMs, ct, r =>
        {
            r.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", Opt("apiKey"));
            var region = Opt("region", "global");
            if (!string.IsNullOrWhiteSpace(region))
                r.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", region);
        });

        var json = Net.Json(body);
        if (json is JsonObject o && o["error"] is { } err)
            throw new ProviderException(err["message"]?.ToString() ?? err.ToString());

        var first = json is JsonArray arr && arr.Count > 0 ? arr[0] : null;
        var translations = first?["translations"] as JsonArray ?? throw new ProviderException("返回格式异常");

        var res = New();
        foreach (var t in translations)
        {
            var to = t?["to"]?.GetValue<string>();
            var text = t?["text"]?.GetValue<string>();
            if (to is null || string.IsNullOrEmpty(text)) continue;
            var canonical = map.TryGetValue(to, out var c) ? c : to;
            res.Texts[canonical] = text;
        }
        if (res.Texts.Count == 0) throw new ProviderException("接口未返回译文");
        res.DetectedFrom = first?["detectedLanguage"]?["language"]?.GetValue<string>();
        return res;
    }
}

/// <summary>谷歌云翻译 API v2（需 API Key）。</summary>
public sealed class GoogleApiTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var to = LangCodes.Google(req.SingleTarget) ?? throw Unsupported(req.SingleTarget);
        var url = "https://translation.googleapis.com/language/translate/v2?key="
                + Uri.EscapeDataString(Opt("apiKey"));

        var payload = new JsonObject { ["q"] = req.Text, ["target"] = to, ["format"] = "text" };
        if (req.From != Languages.Auto)
        {
            var from = LangCodes.Google(req.From);
            if (from is not null) payload["source"] = from;
        }

        var json = Net.Json(await Net.PostJsonAsync(url, payload.ToJsonString(), TimeoutMs, ct));
        if (json["error"] is { } err)
            throw new ProviderException(err["message"]?.ToString() ?? err.ToString());

        var arr = json["data"]?["translations"] as JsonArray ?? throw new ProviderException("返回格式异常");
        var text = arr.Count > 0 ? arr[0]?["translatedText"]?.GetValue<string>() : null;
        if (string.IsNullOrEmpty(text)) throw new ProviderException("接口未返回译文");

        var res = New();
        res.Texts[req.SingleTarget] = System.Net.WebUtility.HtmlDecode(text);
        res.DetectedFrom = arr[0]?["detectedSourceLanguage"]?.GetValue<string>();
        return res;
    }
}
