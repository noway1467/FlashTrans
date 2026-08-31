namespace FlashTrans.Core;

/// <summary>基于字符区块的轻量语种猜测，用于「中↔外自动互译」和需要显式源语言的接口。</summary>
public static class LangDetect
{
    public static string Guess(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "en";
        int han = 0, kana = 0, hangul = 0, cyr = 0, arab = 0, thai = 0, hebrew = 0,
            deva = 0, latin = 0, greek = 0, total = 0;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsDigit(ch) || char.IsSymbol(ch)) continue;
            total++;
            switch (ch)
            {
                case >= '一' and <= '鿿': han++; break;
                case >= '぀' and <= 'ヿ': kana++; break;
                case >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ': hangul++; break;
                case >= 'Ѐ' and <= 'ӿ': cyr++; break;
                case >= '؀' and <= 'ۿ': arab++; break;
                case >= '฀' and <= '๿': thai++; break;
                case >= '֐' and <= '׿': hebrew++; break;
                case >= 'ऀ' and <= 'ॿ': deva++; break;
                case >= 'Ͱ' and <= 'Ͽ': greek++; break;
                case < 'ɐ': latin++; break;
            }
        }
        if (total == 0) return "en";

        // 假名出现即判日语（日文常混大量汉字）
        if (kana > 0 && kana * 20 >= total) return "ja";
        if (hangul * 4 >= total) return "ko";
        if (han * 4 >= total) return "zh-CN";
        if (cyr * 2 >= total) return "ru";
        if (arab * 2 >= total) return "ar";
        if (thai * 2 >= total) return "th";
        if (hebrew * 2 >= total) return "he";
        if (deva * 2 >= total) return "hi";
        if (greek * 2 >= total) return "el";
        if (kana > 0) return "ja";
        if (han > 0) return "zh-CN";
        return latin > 0 ? "en" : "en";
    }

    public static bool IsChinese(string code) =>
        code is "zh-CN" or "zh-TW" or "yue" || code.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否像单词/短语（用于决定是否请求词典释义）。</summary>
    public static bool LooksLikeWord(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Length > 30) return false;
        if (t.Contains('\n')) return false;
        int spaces = t.Count(char.IsWhiteSpace);
        return spaces <= 2;
    }
}
