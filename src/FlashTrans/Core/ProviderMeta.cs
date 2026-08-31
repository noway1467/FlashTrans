namespace FlashTrans.Core;

public static class ProviderMeta
{
    const string DefaultAiPrompt =
        "You are a professional translator. Translate the user's text into {target}. " +
        "Output only the translation, no explanations, no quotes, keep the original " +
        "line breaks, formatting and placeholders intact.";

    static ProviderField Timeout => new("_", "", FieldKind.Number);

    public static readonly ProviderMetaInfo[] All =
    [
        new(ProviderKind.GoogleFree, "谷歌翻译（免费）", "G", "#4285F4",
            "免费 · 无需 Key · 需能访问 Google", "https://translate.google.com",
            [new("host", "接口域名", FieldKind.Text, false, "translate.googleapis.com", "可换成自建/镜像地址")],
            DefaultEnabled: true, NeedsKey: false),

        new(ProviderKind.BingFree, "微软翻译（免费）", "微", "#00A4EF",
            "免费 · 无需 Key · 走 Edge 公共接口", "https://www.bing.com/translator",
            [], DefaultEnabled: true, NeedsKey: false),

        new(ProviderKind.YoudaoFree, "有道翻译（免费）", "有", "#D9282A",
            "免费 · 无需 Key · 演示接口，偶发限流", "https://ai.youdao.com",
            [], DefaultEnabled: true, NeedsKey: false),

        // 和下面的「腾讯翻译君」不是一个源：这个是交互翻译网页接口，免费不要 Key。
        new(ProviderKind.TranSmart, "腾讯交互翻译（免费）", "交", "#3F6FE8",
            "免费 · 无需 Key · 国内直连 · 支持 15 种语言", "https://transmart.qq.com/zh-CN/index",
            [], DefaultEnabled: true, NeedsKey: false),

        new(ProviderKind.Lingva, "Lingva（谷歌镜像）", "L", "#3B7A57",
            "免费 · 无需 Key · 开源 Google 代理", "https://github.com/thedaviddelta/lingva-translate",
            [new("endpoint", "实例地址", FieldKind.Text, false, "https://lingva.ml", "可自建实例")],
            NeedsKey: false),

        new(ProviderKind.MyMemory, "MyMemory", "MM", "#8E6AB8",
            "免费 · 每日 5000 词（填邮箱可提额）", "https://mymemory.translated.net/doc/spec.php",
            [new("email", "邮箱（可选）", FieldKind.Text, false, null, "填写后每日额度更高")],
            NeedsKey: false),

        new(ProviderKind.LibreTranslate, "LibreTranslate", "LT", "#2C8C6B",
            "开源自建免费 · 公共实例需 Key", "https://libretranslate.com",
            [
                new("endpoint", "实例地址", FieldKind.Text, false, "https://libretranslate.com"),
                new("apiKey", "API Key（可选）", FieldKind.Secret)
            ], NeedsKey: false),

        new(ProviderKind.DeepL, "DeepL", "DL", "#0F2B46",
            "Free 版每月 50 万字符免费", "https://www.deepl.com/pro-api",
            [
                new("apiKey", "API Key", FieldKind.Secret, true, null, "Free 版 Key 以 :fx 结尾，自动识别"),
                new("endpoint", "自定义地址（可选）", FieldKind.Text)
            ]),

        new(ProviderKind.DeepLX, "DeepLX（自建）", "DX", "#123C5A",
            "免费 · 本地/自建 DeepLX 服务", "https://github.com/OwO-Network/DeepLX",
            [
                new("endpoint", "服务地址", FieldKind.Text, true, "http://127.0.0.1:1188/translate"),
                new("token", "Token（可选）", FieldKind.Secret)
            ], NeedsKey: false),

        new(ProviderKind.AzureTranslator, "微软 Azure 翻译", "AZ", "#0078D4",
            "F0 免费层每月 200 万字符", "https://portal.azure.com",
            [
                new("apiKey", "订阅密钥", FieldKind.Secret, true),
                new("region", "区域", FieldKind.Text, false, "global", "如 eastasia"),
                new("endpoint", "接口地址", FieldKind.Text, false, "https://api.cognitive.microsofttranslator.com")
            ]),

        new(ProviderKind.GoogleApi, "谷歌云翻译 API", "GC", "#1A73E8",
            "每月 50 万字符免费", "https://cloud.google.com/translate",
            [new("apiKey", "API Key", FieldKind.Secret, true)]),

        new(ProviderKind.Baidu, "百度翻译", "百", "#2932E1",
            "通用版每月有免费额度", "https://fanyi-api.baidu.com",
            [
                new("appId", "APP ID", FieldKind.Text, true),
                new("appKey", "密钥", FieldKind.Secret, true)
            ]),

        new(ProviderKind.Tencent, "腾讯翻译君", "腾", "#00A9F0",
            "每月 500 万字符免费", "https://cloud.tencent.com/product/tmt",
            [
                new("secretId", "SecretId", FieldKind.Text, true),
                new("secretKey", "SecretKey", FieldKind.Secret, true),
                new("region", "区域", FieldKind.Text, false, "ap-beijing")
            ]),

        new(ProviderKind.Caiyun, "彩云小译", "彩", "#3AA9E0",
            "有免费额度 · 仅中英日", "https://dashboard.caiyunapp.com",
            [new("token", "Token", FieldKind.Secret, true)]),

        new(ProviderKind.OpenAiCompat, "AI 翻译（OpenAI 兼容）", "AI", "#10A37F",
            "支持 OpenAI / DeepSeek / Kimi / GLM / Qwen / Ollama 等", "https://platform.openai.com",
            [
                new("baseUrl", "接口地址", FieldKind.Text, true, "https://api.openai.com/v1"),
                new("apiKey", "API Key", FieldKind.Secret, false, null, "本地 Ollama 可留空"),
                new("model", "模型", FieldKind.Text, true, "gpt-4o-mini"),
                new("prompt", "系统提示词", FieldKind.Multiline, false, DefaultAiPrompt, "{target} {source} 会被替换"),
                new("temperature", "温度", FieldKind.Number, false, "0.2"),
                new("stream", "流式输出（打字机效果）", FieldKind.Bool, false, "true")
            ], IsAi: true),

        new(ProviderKind.Gemini, "Google Gemini", "GM", "#886FBF",
            "有免费额度", "https://aistudio.google.com/apikey",
            [
                new("apiKey", "API Key", FieldKind.Secret, true),
                new("model", "模型", FieldKind.Text, true, "gemini-2.0-flash"),
                new("prompt", "系统提示词", FieldKind.Multiline, false, DefaultAiPrompt)
            ], IsAi: true),

        new(ProviderKind.Claude, "Anthropic Claude", "CL", "#D97757",
            "按量计费 · 译文质量高", "https://console.anthropic.com",
            [
                new("apiKey", "API Key", FieldKind.Secret, true),
                new("model", "模型", FieldKind.Text, true, "claude-sonnet-5"),
                new("prompt", "系统提示词", FieldKind.Multiline, false, DefaultAiPrompt)
            ], IsAi: true),
    ];

    static readonly Dictionary<ProviderKind, ProviderMetaInfo> Map = All.ToDictionary(m => m.Kind);

    public static ProviderMetaInfo Get(ProviderKind kind) =>
        Map.TryGetValue(kind, out var m) ? m : All[0];

    public static readonly string[] SecretKeys = ["apiKey", "appKey", "secretKey", "token"];

    public static bool IsSecret(string key) =>
        SecretKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static readonly AiPreset[] AiPresets =
    [
        new("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", "官方接口"),
        new("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", "便宜、中文好"),
        new("Kimi 月之暗面", "https://api.moonshot.cn/v1", "moonshot-v1-8k", "国内直连"),
        new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", "flash 免费"),
        new("通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-turbo", "有免费额度"),
        new("Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", "免费且极快"),
        new("SiliconFlow 硅基流动", "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct", "部分模型免费"),
        new("OpenRouter", "https://openrouter.ai/api/v1", "google/gemini-2.0-flash-exp:free", "含免费模型"),
        new("Ollama（本地）", "http://127.0.0.1:11434/v1", "qwen2.5:7b", "本地离线，无需 Key"),
        new("LM Studio（本地）", "http://127.0.0.1:1234/v1", "local-model", "本地离线"),
    ];

    public static string DefaultPrompt => DefaultAiPrompt;
}
