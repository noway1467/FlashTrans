using System.Text.Json.Nodes;
using FlashTrans.Core;

namespace FlashTrans.Providers;

/// <summary>
/// 腾讯交互翻译 TranSmart（transmart.qq.com）的网页接口，免费、无需 Key。
/// 和「腾讯翻译君」（云 API，要 SecretId/SecretKey）不是一个东西：这个走的是
/// 交互翻译网页自己用的 /api/imt。
/// </summary>
public sealed class TranSmartTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    const string Endpoint = "https://transmart.qq.com/api/imt";

    public override string? ConfigError => null;

    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var to = LangCodes.TranSmart(req.SingleTarget) ?? throw Unsupported(req.SingleTarget);
        var from = LangCodes.TranSmart(req.From) ?? "auto";

        var payload = new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["fn"] = "auto_translation",
                // client_key 必须以 browser- 开头，否则一律 Auth-Failed。
                // 后面跟什么它不校验，这里带上版本号方便对方看日志。
                ["client_key"] = "browser-chrome-138.0.0-Windows-flashtrans",
            },
            ["type"] = "plain",
            ["model_category"] = "normal",
            ["text_domain"] = "",
            ["source"] = new JsonObject
            {
                ["lang"] = from,
                // 整段作为一个元素传。拆成多个元素是逐条独立翻译的，
                // 上下文全丢：「Line one.」会被译成「一号线。」。
                // 元素内部的换行接口会原样保留。
                ["text_list"] = new JsonArray(req.Text),
            },
            ["target"] = new JsonObject { ["lang"] = to },
        }.ToJsonString();

        var body = await Net.PostJsonAsync(Endpoint, payload, TimeoutMs, ct, r =>
        {
            // 网页接口，按浏览器的样子带上来源头，不然容易被挡。
            r.Headers.Referrer = new Uri("https://transmart.qq.com/zh-CN/index");
            r.Headers.TryAddWithoutValidation("Origin", "https://transmart.qq.com");
        });

        var json = Net.Json(body);
        var ret = json["header"]?["ret_code"]?.ToString();
        if (ret is not null && ret != "succ")
            throw new ProviderException(Explain(ret, json));

        var text = json["auto_translation"] switch
        {
            JsonArray a when a.Count > 0 =>
                string.Join("\n", a.Where(x => x is not null).Select(x => x!.GetValue<string>())),
            JsonValue v => v.GetValue<string>(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text)) throw new ProviderException("接口未返回译文");

        var res = New();
        res.Texts[req.SingleTarget] = text;
        // auto 时接口会回显识别到的语言
        var src = json["src_lang"]?.ToString();
        if (!string.IsNullOrWhiteSpace(src)) res.DetectedFrom = src == "zh" ? "zh-CN" : src;
        return res;
    }

    static string Explain(string ret, JsonNode json) => ret switch
    {
        "Auth-Failed" => "接口拒绝了请求（客户端标识失效），请更新版本或换个源",
        "Unsupported-Language" => "该源不支持这个语言方向",
        _ => "TranSmart 返回 " + ret + Detail(json),
    };

    static string Detail(JsonNode json)
    {
        var msg = json["message"]?.ToString() ?? json["header"]?["message"]?.ToString();
        return string.IsNullOrWhiteSpace(msg) ? "" : "：" + msg;
    }
}
