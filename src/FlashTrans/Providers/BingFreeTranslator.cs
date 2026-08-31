using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlashTrans.Core;

namespace FlashTrans.Providers;

/// <summary>微软翻译免费接口（Bing Translator 网页端），无需 Key。自动获取并缓存防滥用 token。</summary>
public sealed partial class BingFreeTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    sealed record Session(string Ig, string Iid, string Key, string Token, DateTime ExpiresAt);

    static Session? _session;
    static readonly SemaphoreSlim Gate = new(1, 1);

    [GeneratedRegex(@"IG:""([A-Fa-f0-9]+)""")] private static partial Regex RxIg();
    [GeneratedRegex(@"data-iid=""([^""]+)""")] private static partial Regex RxIid();
    [GeneratedRegex(@"params_AbusePreventionHelper\s*=\s*\[\s*(\d+)\s*,\s*""([^""]+)""\s*,\s*(\d+)\s*\]")]
    private static partial Regex RxToken();

    public override string? ConfigError => null;

    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var to = LangCodes.Bing(req.SingleTarget) ?? throw Unsupported(req.SingleTarget);
        if (string.IsNullOrEmpty(to)) throw Unsupported(req.SingleTarget);
        var from = LangCodes.Bing(req.From) ?? "";

        var s = await GetSessionAsync(false, ct);
        try
        {
            return await CallAsync(s, req, from, to, ct);
        }
        catch (ProviderException) when (!ct.IsCancellationRequested)
        {
            // token 可能失效，强制刷新一次再试
            s = await GetSessionAsync(true, ct);
            return await CallAsync(s, req, from, to, ct);
        }
    }

    async Task<TranslateResult> CallAsync(Session s, TranslateRequest req, string from, string to, CancellationToken ct)
    {
        var url = $"https://www.bing.com/ttranslatev3?isVertical=1&&IG={s.Ig}&IID={s.Iid}";
        var form = new List<KeyValuePair<string, string>>
        {
            new("fromLang", string.IsNullOrEmpty(from) ? "auto-detect" : from),
            new("to", to),
            new("text", req.Text),
            new("token", s.Token),
            new("key", s.Key),
            new("tryFetchingGenderDebiasedTranslations", "true"),
        };

        var body = await Net.PostFormAsync(url, form, TimeoutMs, ct, r =>
        {
            r.Headers.Referrer = new Uri("https://www.bing.com/translator");
            r.Headers.TryAddWithoutValidation("Origin", "https://www.bing.com");
        });

        var json = Net.Json(body);
        if (json is JsonObject obj && obj.ContainsKey("statusCode"))
            throw new ProviderException("接口返回错误 " + obj["statusCode"]);

        var first = json is JsonArray arr && arr.Count > 0 ? arr[0] : json;
        var translations = first?["translations"] as JsonArray
            ?? throw new ProviderException("返回格式异常");
        var text = translations.Count > 0 ? translations[0]?["text"]?.GetValue<string>() : null;
        if (string.IsNullOrEmpty(text)) throw new ProviderException("接口未返回译文");

        var res = New();
        res.Texts[req.SingleTarget] = text;
        res.DetectedFrom = first?["detectedLanguage"]?["language"]?.GetValue<string>();
        return res;
    }

    async Task<Session> GetSessionAsync(bool force, CancellationToken ct)
    {
        var cur = _session;
        if (!force && cur is not null && cur.ExpiresAt > DateTime.UtcNow) return cur;

        await Gate.WaitAsync(ct);
        try
        {
            cur = _session;
            if (!force && cur is not null && cur.ExpiresAt > DateTime.UtcNow) return cur;

            var page = await Net.GetStringAsync("https://www.bing.com/translator", TimeoutMs, ct);
            var ig = RxIg().Match(page);
            var iid = RxIid().Match(page);
            var tok = RxToken().Match(page);
            if (!ig.Success || !tok.Success)
                throw new ProviderException("无法获取微软翻译会话（接口可能已变更）");

            var expiresMs = long.TryParse(tok.Groups[3].Value, out var e) ? e : 600_000;
            var session = new Session(
                ig.Groups[1].Value,
                iid.Success ? iid.Groups[1].Value : "translator.5023",
                tok.Groups[1].Value,
                tok.Groups[2].Value,
                DateTime.UtcNow.AddMilliseconds(Math.Min(expiresMs, 1_800_000)).AddSeconds(-30));
            _session = session;
            return session;
        }
        finally { Gate.Release(); }
    }
}
