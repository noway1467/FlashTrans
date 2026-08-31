using System.Text.Json.Nodes;
using FlashTrans.Core;

namespace FlashTrans.Providers;

/// <summary>有道翻译免费演示接口，无需 Key，单词可返回音标与释义。</summary>
public sealed class YoudaoFreeTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    public override string? ConfigError => null;

    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var to = LangCodes.Youdao(req.SingleTarget) ?? throw Unsupported(req.SingleTarget);
        var from = LangCodes.Youdao(req.From) ?? "auto";

        var body = await Net.PostFormAsync("https://aidemo.youdao.com/trans",
            [new("q", req.Text), new("from", from), new("to", to)], TimeoutMs, ct);

        var json = Net.Json(body);
        var code = json["errorCode"]?.ToString();
        if (code is not null && code != "0")
            throw new ProviderException($"有道错误码 {code}{ErrorHint(code)}");

        var res = New();
        if (json["translation"] is JsonArray arr && arr.Count > 0)
        {
            var text = string.Join("\n", arr.Where(x => x is not null).Select(x => x!.GetValue<string>()));
            if (!string.IsNullOrWhiteSpace(text)) res.Texts[req.SingleTarget] = text;
        }
        if (res.Texts.Count == 0) throw new ProviderException("接口未返回译文");

        var basic = json["basic"];
        if (basic is not null)
        {
            res.Phonetic = basic["us-phonetic"]?.GetValue<string>()
                        ?? basic["uk-phonetic"]?.GetValue<string>()
                        ?? basic["phonetic"]?.GetValue<string>();
            if (basic["explains"] is JsonArray ex && ex.Count > 0)
            {
                res.Dict = [];
                foreach (var e in ex.Take(6))
                {
                    var s = e?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    var idx = s.IndexOf(' ');
                    var isPos = idx is > 0 and <= 6 && s[..idx].EndsWith('.');
                    res.Dict.Add(new DictEntry
                    {
                        Pos = isPos ? s[..idx] : "",
                        Terms = [isPos ? s[(idx + 1)..].Trim() : s.Trim()]
                    });
                }
            }
        }

        var l = json["l"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(l) && l.Contains('2'))
            res.DetectedFrom = l[..l.IndexOf('2')];
        return res;
    }

    static string ErrorHint(string code) => code switch
    {
        "108" => "（演示接口暂不可用）",
        "111" => "（账号无效）",
        "202" => "（签名错误）",
        "401" => "（账户欠费）",
        "411" => "（请求过于频繁，稍后再试）",
        _ => ""
    };
}
