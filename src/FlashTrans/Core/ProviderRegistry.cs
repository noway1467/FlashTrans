using FlashTrans.Providers;

namespace FlashTrans.Core;

/// <summary>按配置创建并缓存 <see cref="ITranslator"/> 实例，配置未变则复用。</summary>
public sealed class ProviderRegistry
{
    readonly Dictionary<string, (string Sig, ITranslator Impl)> _cache = new(StringComparer.Ordinal);
    readonly object _gate = new();

    public ITranslator Get(ProviderConfig cfg)
    {
        var sig = Signature(cfg);
        lock (_gate)
        {
            if (_cache.TryGetValue(cfg.Id, out var hit) && hit.Sig == sig) return hit.Impl;
            var impl = Create(cfg);
            _cache[cfg.Id] = (sig, impl);
            return impl;
        }
    }

    public void Invalidate()
    {
        lock (_gate) _cache.Clear();
    }

    static string Signature(ProviderConfig cfg)
    {
        var opts = string.Join(';', cfg.Options.OrderBy(k => k.Key, StringComparer.Ordinal)
                                              .Select(k => k.Key + '=' + k.Value));
        return $"{cfg.Kind}|{cfg.Name}|{cfg.TimeoutMs}|{opts}";
    }

    static ITranslator Create(ProviderConfig cfg) => cfg.Kind switch
    {
        ProviderKind.GoogleFree => new GoogleFreeTranslator(cfg),
        ProviderKind.GoogleApi => new GoogleApiTranslator(cfg),
        ProviderKind.BingFree => new BingFreeTranslator(cfg),
        ProviderKind.AzureTranslator => new AzureTranslatorProvider(cfg),
        ProviderKind.DeepL => new DeepLTranslator(cfg),
        ProviderKind.DeepLX => new DeepLXTranslator(cfg),
        ProviderKind.YoudaoFree => new YoudaoFreeTranslator(cfg),
        ProviderKind.Baidu => new BaiduTranslator(cfg),
        ProviderKind.Tencent => new TencentTranslator(cfg),
        ProviderKind.Caiyun => new CaiyunTranslator(cfg),
        ProviderKind.LibreTranslate => new LibreTranslateTranslator(cfg),
        ProviderKind.MyMemory => new MyMemoryTranslator(cfg),
        ProviderKind.Lingva => new LingvaTranslator(cfg),
        ProviderKind.TranSmart => new TranSmartTranslator(cfg),
        ProviderKind.OpenAiCompat => new OpenAiCompatTranslator(cfg),
        ProviderKind.Gemini => new GeminiTranslator(cfg),
        ProviderKind.Claude => new ClaudeTranslator(cfg),
        _ => throw new NotSupportedException("未知翻译源类型：" + cfg.Kind)
    };

    /// <summary>预热常用免费源的 TLS 连接。</summary>
    public static void WarmupFor(IEnumerable<ProviderConfig> providers)
    {
        var urls = new List<string>();
        foreach (var p in providers.Where(p => p.Enabled))
        {
            switch (p.Kind)
            {
                case ProviderKind.GoogleFree: urls.Add("https://translate.googleapis.com/"); break;
                case ProviderKind.BingFree: urls.Add("https://www.bing.com/translator"); break;
                case ProviderKind.YoudaoFree: urls.Add("https://aidemo.youdao.com/"); break;
                case ProviderKind.TranSmart: urls.Add("https://transmart.qq.com/"); break;
                case ProviderKind.DeepL: urls.Add("https://api-free.deepl.com/"); break;
            }
        }
        if (urls.Count > 0) Net.Warmup(urls.Distinct());
    }
}
