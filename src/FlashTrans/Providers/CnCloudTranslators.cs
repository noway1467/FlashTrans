using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FlashTrans.Core;

namespace FlashTrans.Providers;

/// <summary>百度翻译开放平台（通用文本翻译）。</summary>
public sealed class BaiduTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var appId = Opt("appId");
        var appKey = Opt("appKey");
        var to = LangCodes.Baidu(req.SingleTarget) ?? throw Unsupported(req.SingleTarget);
        var from = LangCodes.Baidu(req.From) ?? "auto";

        var salt = Random.Shared.Next(100000, 999999).ToString();
        var sign = Md5Hex(appId + req.Text + salt + appKey);

        var body = await Net.PostFormAsync("https://fanyi-api.baidu.com/api/trans/vip/translate",
        [
            new("q", req.Text), new("from", from), new("to", to),
            new("appid", appId), new("salt", salt), new("sign", sign)
        ], TimeoutMs, ct);

        var json = Net.Json(body);
        if (json["error_code"] is { } ec)
            throw new ProviderException($"百度错误 {ec}：{json["error_msg"]?.ToString() ?? ""}".Trim());

        var arr = json["trans_result"] as JsonArray ?? throw new ProviderException("返回格式异常");
        var text = string.Join("\n", arr.Where(x => x?["dst"] is not null).Select(x => x!["dst"]!.GetValue<string>()));
        if (string.IsNullOrWhiteSpace(text)) throw new ProviderException("接口未返回译文");

        var res = New();
        res.Texts[req.SingleTarget] = text;
        res.DetectedFrom = json["from"]?.ToString();
        return res;
    }

    static string Md5Hex(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

/// <summary>腾讯云机器翻译 TMT（TC3-HMAC-SHA256 签名）。</summary>
public sealed class TencentTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    const string Host = "tmt.tencentcloudapi.com";
    const string Service = "tmt";
    const string Action = "TextTranslate";
    const string Version = "2018-03-21";
    const string ContentType = "application/json; charset=utf-8";

    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var to = LangCodes.Tencent(req.SingleTarget) ?? throw Unsupported(req.SingleTarget);
        var from = LangCodes.Tencent(req.From) ?? "auto";

        var payload = new JsonObject
        {
            ["SourceText"] = req.Text,
            ["Source"] = from,
            ["Target"] = to,
            ["ProjectId"] = 0,
        }.ToJsonString();

        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds().ToString();
        var date = now.UtcDateTime.ToString("yyyy-MM-dd");
        var auth = BuildAuth(payload, ts, date);

        var body = await Net.PostJsonAsync($"https://{Host}/", payload, TimeoutMs, ct, r =>
        {
            r.Content!.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            r.Headers.TryAddWithoutValidation("Authorization", auth);
            r.Headers.TryAddWithoutValidation("X-TC-Action", Action);
            r.Headers.TryAddWithoutValidation("X-TC-Version", Version);
            r.Headers.TryAddWithoutValidation("X-TC-Timestamp", ts);
            var region = Opt("region", "ap-beijing");
            if (!string.IsNullOrWhiteSpace(region)) r.Headers.TryAddWithoutValidation("X-TC-Region", region);
        });

        var json = Net.Json(body)["Response"] ?? throw new ProviderException("返回格式异常");
        if (json["Error"] is { } err)
            throw new ProviderException($"{err["Code"]}：{err["Message"]}");

        var text = json["TargetText"]?.GetValue<string>();
        if (string.IsNullOrEmpty(text)) throw new ProviderException("接口未返回译文");

        var res = New();
        res.Texts[req.SingleTarget] = text;
        res.DetectedFrom = json["Source"]?.ToString();
        return res;
    }

    string BuildAuth(string payload, string ts, string date)
    {
        const string signedHeaders = "content-type;host;x-tc-action";
        var canonicalHeaders = $"content-type:{ContentType}\nhost:{Host}\nx-tc-action:{Action.ToLowerInvariant()}\n";
        var canonicalRequest = string.Join('\n',
            "POST", "/", "", canonicalHeaders, signedHeaders, Sha256Hex(payload));

        var scope = $"{date}/{Service}/tc3_request";
        var stringToSign = string.Join('\n', "TC3-HMAC-SHA256", ts, scope, Sha256Hex(canonicalRequest));

        var kDate = Hmac(Encoding.UTF8.GetBytes("TC3" + Opt("secretKey")), date);
        var kService = Hmac(kDate, Service);
        var kSigning = Hmac(kService, "tc3_request");
        var signature = Convert.ToHexString(Hmac(kSigning, stringToSign)).ToLowerInvariant();

        return $"TC3-HMAC-SHA256 Credential={Opt("secretId")}/{scope}, " +
               $"SignedHeaders={signedHeaders}, Signature={signature}";
    }

    static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

/// <summary>彩云小译（仅中英日互译）。</summary>
public sealed class CaiyunTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var to = LangCodes.Caiyun(req.SingleTarget) ?? throw Unsupported(req.SingleTarget);
        var from = LangCodes.Caiyun(req.From) ?? "auto";
        if (from == to) throw new ProviderException("源语言与目标语言相同");

        var payload = new JsonObject
        {
            ["source"] = new JsonArray(req.Text),
            ["trans_type"] = $"{from}2{to}",
            ["request_id"] = "flashtrans",
            ["detect"] = true,
        }.ToJsonString();

        var body = await Net.PostJsonAsync("https://api.interpreter.caiyunai.com/v1/translator",
            payload, TimeoutMs, ct,
            r => r.Headers.TryAddWithoutValidation("x-authorization", "token " + Opt("token")));

        var json = Net.Json(body);
        if (json["message"] is { } msg && json["target"] is null)
            throw new ProviderException(msg.ToString());

        var text = json["target"] switch
        {
            JsonArray a when a.Count > 0 => a[0]?.GetValue<string>(),
            JsonValue v => v.GetValue<string>(),
            _ => null
        };
        if (string.IsNullOrEmpty(text)) throw new ProviderException("接口未返回译文");

        var res = New();
        res.Texts[req.SingleTarget] = text;
        return res;
    }
}
