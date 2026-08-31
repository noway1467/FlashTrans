namespace FlashTrans.Core;

public sealed record Lang(string Code, string NameZh, string NameEn)
{
    public override string ToString() => NameZh;
}

public static class Languages
{
    public const string Auto = "auto";
    public static readonly Lang AutoLang = new(Auto, "自动检测", "Auto");

    public static readonly Lang[] All =
    [
        new("zh-CN", "简体中文", "Chinese (Simplified)"),
        new("zh-TW", "繁体中文", "Chinese (Traditional)"),
        new("yue", "粤语", "Cantonese"),
        new("en", "英语", "English"),
        new("ja", "日语", "Japanese"),
        new("ko", "韩语", "Korean"),
        new("fr", "法语", "French"),
        new("de", "德语", "German"),
        new("es", "西班牙语", "Spanish"),
        new("ru", "俄语", "Russian"),
        new("pt", "葡萄牙语", "Portuguese"),
        new("it", "意大利语", "Italian"),
        new("ar", "阿拉伯语", "Arabic"),
        new("hi", "印地语", "Hindi"),
        new("th", "泰语", "Thai"),
        new("vi", "越南语", "Vietnamese"),
        new("id", "印尼语", "Indonesian"),
        new("ms", "马来语", "Malay"),
        new("tr", "土耳其语", "Turkish"),
        new("pl", "波兰语", "Polish"),
        new("nl", "荷兰语", "Dutch"),
        new("sv", "瑞典语", "Swedish"),
        new("da", "丹麦语", "Danish"),
        new("fi", "芬兰语", "Finnish"),
        new("nb", "挪威语", "Norwegian"),
        new("cs", "捷克语", "Czech"),
        new("el", "希腊语", "Greek"),
        new("he", "希伯来语", "Hebrew"),
        new("hu", "匈牙利语", "Hungarian"),
        new("ro", "罗马尼亚语", "Romanian"),
        new("uk", "乌克兰语", "Ukrainian"),
        new("bg", "保加利亚语", "Bulgarian"),
        new("sk", "斯洛伐克语", "Slovak"),
        new("sl", "斯洛文尼亚语", "Slovenian"),
        new("hr", "克罗地亚语", "Croatian"),
        new("sr", "塞尔维亚语", "Serbian"),
        new("et", "爱沙尼亚语", "Estonian"),
        new("lv", "拉脱维亚语", "Latvian"),
        new("lt", "立陶宛语", "Lithuanian"),
        new("fa", "波斯语", "Persian"),
        new("ur", "乌尔都语", "Urdu"),
        new("bn", "孟加拉语", "Bengali"),
        new("ta", "泰米尔语", "Tamil"),
        new("te", "泰卢固语", "Telugu"),
        new("mr", "马拉地语", "Marathi"),
        new("kn", "卡纳达语", "Kannada"),
        new("ml", "马拉雅拉姆语", "Malayalam"),
        new("gu", "古吉拉特语", "Gujarati"),
        new("pa", "旁遮普语", "Punjabi"),
        new("ne", "尼泊尔语", "Nepali"),
        new("si", "僧伽罗语", "Sinhala"),
        new("km", "高棉语", "Khmer"),
        new("lo", "老挝语", "Lao"),
        new("my", "缅甸语", "Burmese"),
        new("mn", "蒙古语", "Mongolian"),
        new("tl", "菲律宾语", "Filipino"),
        new("sw", "斯瓦希里语", "Swahili"),
        new("af", "南非荷兰语", "Afrikaans"),
        new("sq", "阿尔巴尼亚语", "Albanian"),
        new("hy", "亚美尼亚语", "Armenian"),
        new("az", "阿塞拜疆语", "Azerbaijani"),
        new("eu", "巴斯克语", "Basque"),
        new("be", "白俄罗斯语", "Belarusian"),
        new("ca", "加泰罗尼亚语", "Catalan"),
        new("gl", "加利西亚语", "Galician"),
        new("ka", "格鲁吉亚语", "Georgian"),
        new("is", "冰岛语", "Icelandic"),
        new("ga", "爱尔兰语", "Irish"),
        new("kk", "哈萨克语", "Kazakh"),
        new("uz", "乌兹别克语", "Uzbek"),
        new("la", "拉丁语", "Latin"),
        new("eo", "世界语", "Esperanto"),
    ];

    static readonly Dictionary<string, Lang> ByCode =
        All.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);

    public static Lang Get(string code)
    {
        if (string.IsNullOrEmpty(code) || code == Auto) return AutoLang;
        if (ByCode.TryGetValue(code, out var l)) return l;
        return new Lang(code, code, code);
    }

    public static string NameOf(string code) => Get(code).NameZh;
    public static string EnglishNameOf(string code) => Get(code).NameEn;

    public static IReadOnlyList<Lang> WithAuto()
    {
        var list = new List<Lang>(All.Length + 1) { AutoLang };
        list.AddRange(All);
        return list;
    }
}
