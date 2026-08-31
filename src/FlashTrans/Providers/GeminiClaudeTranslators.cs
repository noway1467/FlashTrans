using System.Text.Json.Nodes;
using FlashTrans.Core;

namespace FlashTrans.Providers;

/// <summary>Google Gemini（generateContent）。</summary>
public sealed class GeminiTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var model = Opt("model", "gemini-2.0-flash");
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}"
                + $":generateContent?key={Uri.EscapeDataString(Opt("apiKey"))}";

        var payload = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = AiPrompt.Build(Opt("prompt"), req) })
            },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = req.Text })
            }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.2 },
        };

        var json = Net.Json(await Net.PostJsonAsync(url, payload.ToJsonString(), TimeoutMs, ct));
        if (json["error"] is { } err)
            throw new ProviderException(err["message"]?.ToString() ?? err.ToString());

        var candidate = (json["candidates"] as JsonArray)?.FirstOrDefault();
        if (candidate is null)
        {
            var reason = json["promptFeedback"]?["blockReason"]?.ToString();
            throw new ProviderException(reason is null ? "模型未返回译文" : "被模型拦截：" + reason);
        }

        var parts = candidate["content"]?["parts"] as JsonArray;
        var text = string.Concat(parts?.Select(p => p?["text"]?.GetValue<string>() ?? "") ?? []);
        if (string.IsNullOrWhiteSpace(text)) throw new ProviderException("模型未返回译文");

        var res = New();
        res.Texts[req.SingleTarget] = AiPrompt.Cleanup(text);
        return res;
    }
}

/// <summary>Anthropic Claude（messages API）。</summary>
public sealed class ClaudeTranslator(ProviderConfig cfg) : TranslatorBase(cfg)
{
    protected override async Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = Opt("model", "claude-sonnet-5"),
            ["max_tokens"] = 4096,
            ["temperature"] = 0.2,
            ["system"] = AiPrompt.Build(Opt("prompt"), req),
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = req.Text
            }),
        };

        var body = await Net.PostJsonAsync("https://api.anthropic.com/v1/messages",
            payload.ToJsonString(), TimeoutMs, ct, r =>
            {
                r.Headers.TryAddWithoutValidation("x-api-key", Opt("apiKey"));
                r.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            });

        var json = Net.Json(body);
        if (json["error"] is { } err)
            throw new ProviderException(err["message"]?.ToString() ?? err.ToString());

        var blocks = json["content"] as JsonArray;
        var text = string.Concat(blocks?
            .Where(b => b?["type"]?.ToString() == "text")
            .Select(b => b!["text"]?.GetValue<string>() ?? "") ?? []);
        if (string.IsNullOrWhiteSpace(text)) throw new ProviderException("模型未返回译文");

        var res = New();
        res.Texts[req.SingleTarget] = AiPrompt.Cleanup(text);
        return res;
    }
}
