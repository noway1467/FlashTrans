namespace FlashTrans.Core;

/// <summary>把统一语言代码（Google 风格）翻译成各家接口自己的代码。</summary>
public static class LangCodes
{
    static Dictionary<string, string> D(params (string k, string v)[] items)
        => items.ToDictionary(i => i.k, i => i.v, StringComparer.OrdinalIgnoreCase);

    // ---------- Google / Lingva ----------
    static readonly Dictionary<string, string> GoogleMap = D(("he", "iw"), ("nb", "no"), ("tl", "tl"));

    public static string? Google(string code) =>
        code == Languages.Auto ? "auto" : GoogleMap.GetValueOrDefault(code, code);

    // ---------- Bing / Azure ----------
    static readonly Dictionary<string, string> BingMap = D(
        ("zh-CN", "zh-Hans"), ("zh-TW", "zh-Hant"), ("tl", "fil"), ("sr", "sr-Cyrl"), ("mn", "mn-Cyrl"));

    public static string? Bing(string code) =>
        code == Languages.Auto ? "" : BingMap.GetValueOrDefault(code, code);

    // ---------- DeepL ----------
    static readonly HashSet<string> DeepLSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar","bg","cs","da","de","el","en","es","et","fi","fr","hu","id","it","ja","ko",
        "lt","lv","nb","nl","pl","pt","ro","ru","sk","sl","sv","tr","uk","zh-CN","zh-TW"
    };

    public static string? DeepL(string code, bool target)
    {
        if (code == Languages.Auto) return null; // 交给接口自动识别
        if (!DeepLSet.Contains(code)) return null;
        return code switch
        {
            "zh-CN" => target ? "ZH-HANS" : "ZH",
            "zh-TW" => target ? "ZH-HANT" : "ZH",
            "en" => target ? "EN-US" : "EN",
            "pt" => target ? "PT-PT" : "PT",
            _ => code.ToUpperInvariant()
        };
    }

    // ---------- 有道 ----------
    static readonly Dictionary<string, string> YoudaoMap = D(
        ("zh-CN", "zh-CHS"), ("zh-TW", "zh-CHT"), ("yue", "yue"), ("ja", "ja"), ("nb", "no"), ("tl", "tl"));

    public static string? Youdao(string code) =>
        code == Languages.Auto ? "auto" : YoudaoMap.GetValueOrDefault(code, code);

    // ---------- 百度 ----------
    static readonly Dictionary<string, string> BaiduMap = D(
        ("zh-CN", "zh"), ("zh-TW", "cht"), ("yue", "yue"), ("ja", "jp"), ("ko", "kor"), ("fr", "fra"),
        ("es", "spa"), ("ar", "ara"), ("bg", "bul"), ("et", "est"), ("da", "dan"), ("fi", "fin"),
        ("ro", "rom"), ("sl", "slo"), ("sv", "swe"), ("vi", "vie"), ("hi", "hi"), ("nb", "nor"),
        ("cs", "cs"), ("uk", "ukr"), ("fa", "per"), ("he", "heb"), ("id", "id"), ("ms", "may"),
        ("tl", "fil"), ("my", "bur"), ("ta", "tam"), ("te", "tel"), ("km", "hkm"), ("lo", "lao"));

    public static string? Baidu(string code) =>
        code == Languages.Auto ? "auto" : BaiduMap.GetValueOrDefault(code, code);

    // ---------- 腾讯 ----------
    static readonly HashSet<string> TencentSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "zh-CN","zh-TW","en","ja","ko","fr","es","it","de","tr","ru","pt","vi","id","th","ms","ar","hi"
    };

    public static string? Tencent(string code)
    {
        if (code == Languages.Auto) return "auto";
        if (!TencentSet.Contains(code)) return null;
        return code == "zh-CN" ? "zh" : code;
    }

    // ---------- 腾讯交互翻译 TranSmart ----------
    // 实测支持的 15 种。别往里加没验过的：不支持的语言接口会回
    // Unsupported-Language，而这里返回 null 能提前给出「该源不支持…」。
    // zh-TW 是真的繁体（返回「機器學習」这类字形），不是简体糊弄。
    static readonly HashSet<string> TranSmartSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "zh-CN","zh-TW","en","ja","ko","fr","de","es","ru","it","pt","tr","vi","th","ar"
    };

    public static string? TranSmart(string code)
    {
        if (code == Languages.Auto) return "auto";
        if (!TranSmartSet.Contains(code)) return null;
        return code == "zh-CN" ? "zh" : code;
    }

    // ---------- 彩云小译（仅中英日） ----------
    public static string? Caiyun(string code) => code switch
    {
        Languages.Auto => "auto",
        "zh-CN" => "zh", "en" => "en", "ja" => "ja",
        _ => null
    };

    // ---------- LibreTranslate ----------
    static readonly Dictionary<string, string> LibreMap = D(("zh-CN", "zh"), ("zh-TW", "zt"));

    public static string? Libre(string code) =>
        code == Languages.Auto ? "auto" : LibreMap.GetValueOrDefault(code, code);

    // ---------- MyMemory（必须显式源语言） ----------
    public static string? MyMemory(string code) => code == Languages.Auto ? null : code;

    /// <summary>AI 接口用自然语言名称效果最好。</summary>
    public static string AiName(string code) =>
        code == Languages.Auto ? "the original language" : Languages.EnglishNameOf(code);
}
