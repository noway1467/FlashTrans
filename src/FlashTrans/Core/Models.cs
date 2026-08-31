using System.Text.Json.Serialization;

namespace FlashTrans.Core;

/// <summary>翻译源类型。每个类型对应一个 <see cref="ITranslator"/> 实现，可创建多个实例。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProviderKind>))]
public enum ProviderKind
{
    GoogleFree, GoogleApi, BingFree, AzureTranslator,
    DeepL, DeepLX, YoudaoFree, Baidu, Tencent, Caiyun,
    LibreTranslate, MyMemory, Lingva, TranSmart,
    OpenAiCompat, Gemini, Claude
}

public sealed class TranslateRequest
{
    public string Text = "";
    public string From = Languages.Auto;
    public IReadOnlyList<string> Targets = [];
    /// <summary>单词/短语查询，希望返回音标与词典释义。</summary>
    public bool WantDictionary;
    /// <summary>AI 源的附加风格要求。</summary>
    public string? Style;

    public string SingleTarget => Targets.Count > 0 ? Targets[0] : "zh-CN";
}

public sealed class DictEntry
{
    public string Pos = "";
    public List<string> Terms = [];
}

public sealed class TranslateResult
{
    public string ProviderId = "";
    public string ProviderName = "";
    public string? DetectedFrom;
    /// <summary>目标语言代码 -> 译文。</summary>
    public Dictionary<string, string> Texts = new(StringComparer.OrdinalIgnoreCase);
    public string? Phonetic;
    public List<DictEntry>? Dict;
    public long ElapsedMs;
    public string? Error;
    public bool FromCache;

    public bool Ok => Error is null && Texts.Count > 0;

    public string? Get(string lang) => Texts.TryGetValue(lang, out var v) ? v : null;

    public static TranslateResult Failed(ITranslator p, string error, long ms = 0) => new()
    {
        ProviderId = p.Id, ProviderName = p.Name, Error = error, ElapsedMs = ms
    };
}

/// <summary>引擎一次翻译的完整产出。</summary>
public sealed class TranslateBatch
{
    public string SourceText = "";
    public string From = Languages.Auto;
    public List<string> Targets = [];
    /// <summary>按翻译源顺序排列的结果（含失败项）。</summary>
    public List<TranslateResult> Results = [];
    /// <summary>被跳过/降级的说明，用于状态栏。</summary>
    public List<string> Notes = [];
    public long TotalMs;
}

public sealed class ProviderException(string message) : Exception(message);
