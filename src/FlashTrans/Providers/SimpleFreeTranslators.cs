using System.Text.Json.Nodes;
using FlashTrans.Core;

namespace FlashTrans.Providers;

/// <summary>Lingva：开源的谷歌翻译代理，无需 Key。</summary>
public sealed class LingvaTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    public override string? ConfigError => null;

    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var host = Opt("endpoint", "https://lingva.ml").TrimEnd('/');
        var to = LangCodes.Google(req.SingleTarget) ?? throw Unsupported(req.SingleTarget);
        var from = LangCodes.Google(req.From) ?? "auto";
        var url = $"{host}/api/v1/{from}/{to}/{Uri.EscapeDataString(req.Text)}";

        var json = Net.Json(await Net.GetStringAsync(url, TimeoutMs, ct));
        var text = json["translation"]?.GetValue<string>()
            ?? throw new ProviderException("接口未返回译文");

        var res = New();
        res.Texts[req.SingleTarget] = text;
        var info = json["info"];
        res.Phonetic = info?["pronunciation"]?["query"]?.GetValue<string>();
        res.DetectedFrom = info?["detectedSource"]?.GetValue<string>();
        return res;
    }
}

/// <summary>MyMemory：免费翻译记忆库，需要明确源语言（auto 时本地判定）。</summary>
public sealed class MyMemoryTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    public override string? ConfigError => null;

    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        if (req.Text.Length > 500) throw new ProviderException("MyMemory 单次最多 500 字符");
        var to = req.SingleTarget;
        var from = req.From == Languages.Auto ? LangDetect.Guess(req.Text) : req.From;
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            throw new ProviderException("源语言与目标语言相同");

        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(req.Text)}"
                + $"&langpair={Uri.EscapeDataString(from)}|{Uri.EscapeDataString(to)}";
        var email = Opt("email");
        if (!string.IsNullOrWhiteSpace(email)) url += "&de=" + Uri.EscapeDataString(email);

        var json = Net.Json(await Net.GetStringAsync(url, TimeoutMs, ct));
        var status = json["responseStatus"]?.ToString();
        if (status is not null && status != "200")
            throw new ProviderException(json["responseDetails"]?.ToString() ?? ("状态 " + status));

        var text = json["responseData"]?["translatedText"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)) throw new ProviderException("接口未返回译文");

        var res = New();
        res.Texts[to] = System.Net.WebUtility.HtmlDecode(text);
        res.DetectedFrom = from;
        return res;
    }
}

/// <summary>LibreTranslate：开源自建，公共实例可能需要 Key。</summary>
public sealed class LibreTranslateTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    public override string? ConfigError =>
        string.IsNullOrWhiteSpace(Opt("endpoint")) ? "请先填写「实例地址」" : null;

    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var host = Opt("endpoint", "https://libretranslate.com").TrimEnd('/');
        var to = LangCodes.Libre(req.SingleTarget) ?? throw Unsupported(req.SingleTarget);
        var from = LangCodes.Libre(req.From) ?? "auto";

        var payload = new JsonObject
        {
            ["q"] = req.Text,
            ["source"] = from,
            ["target"] = to,
            ["format"] = "text",
        };
        var key = Opt("apiKey");
        if (!string.IsNullOrWhiteSpace(key)) payload["api_key"] = key;

        var json = Net.Json(await Net.PostJsonAsync($"{host}/translate", payload.ToJsonString(), TimeoutMs, ct));
        if (json["error"] is { } err) throw new ProviderException(err.ToString());

        var text = json["translatedText"]?.GetValue<string>()
            ?? throw new ProviderException("接口未返回译文");

        var res = New();
        res.Texts[req.SingleTarget] = text;
        res.DetectedFrom = json["detectedLanguage"]?["language"]?.GetValue<string>();
        return res;
    }
}
