using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using FlashTrans.Core;

namespace FlashTrans.Providers;

/// <summary>谷歌翻译免费接口（translate_a/single），无需 Key，附带音标与词典释义。</summary>
public sealed class GoogleFreeTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    public override string? ConfigError => null;

    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var host = Opt("host", "translate.googleapis.com").Replace("https://", "").Replace("http://", "").TrimEnd('/');
        var target = LangCodes.Google(req.SingleTarget) ?? throw Unsupported(req.SingleTarget);
        var from = LangCodes.Google(req.From) ?? "auto";

        var sb = new StringBuilder($"https://{host}/translate_a/single?client=gtx&dj=1&dt=t&dt=rm");
        if (req.WantDictionary) sb.Append("&dt=bd&dt=ex");
        sb.Append("&sl=").Append(Uri.EscapeDataString(from));
        sb.Append("&tl=").Append(Uri.EscapeDataString(target));
        var url = sb.ToString();

        string body;
        if (req.Text.Length < 1200)
            body = await Net.GetStringAsync(url + "&q=" + Uri.EscapeDataString(req.Text), TimeoutMs, ct);
        else
            body = await Net.PostFormAsync(url, [new("q", req.Text)], TimeoutMs, ct);

        var json = Net.Json(body);
        var res = New();
        var text = new StringBuilder();
        string? phonetic = null;

        if (json["sentences"] is JsonArray sentences)
        {
            foreach (var s in sentences)
            {
                if (s?["trans"] is { } t) text.Append(t.GetValue<string>());
                if (phonetic is null && s?["src_translit"] is { } st)
                {
                    var v = st.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(v)) phonetic = v;
                }
            }
        }
        if (text.Length == 0) throw new ProviderException("接口未返回译文");

        res.Texts[req.SingleTarget] = text.ToString().Trim();
        res.Phonetic = phonetic;
        res.DetectedFrom = json["src"]?.GetValue<string>();

        if (json["dict"] is JsonArray dict && dict.Count > 0)
        {
            res.Dict = [];
            foreach (var d in dict.Take(4))
            {
                var entry = new DictEntry { Pos = d?["pos"]?.GetValue<string>() ?? "" };
                if (d?["terms"] is JsonArray terms)
                    foreach (var t in terms.Take(6))
                        if (t is not null) entry.Terms.Add(t.GetValue<string>());
                if (entry.Terms.Count > 0) res.Dict.Add(entry);
            }
        }
        return res;
    }
}
