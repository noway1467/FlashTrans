namespace FlashTrans.Core;

public interface ITranslator
{
    string Id { get; }
    string Name { get; }
    ProviderKind Kind { get; }
    int TimeoutMs { get; }
    /// <summary>true 表示一次请求可返回多个目标语言，引擎不再拆分并发。</summary>
    bool BatchTargets { get; }
    /// <summary>缺少必填配置时返回原因，引擎会跳过该源。</summary>
    string? ConfigError { get; }

    Task<TranslateResult> TranslateAsync(TranslateRequest req, CancellationToken ct);
}

/// <summary>AI 类接口可流式输出，单源单目标时用于"边出边显"。</summary>
public interface IStreamingTranslator
{
    IAsyncEnumerable<string> StreamAsync(TranslateRequest req, CancellationToken ct);
}

public abstract class TranslatorBase(ProviderConfig cfg) : ITranslator
{
    protected readonly ProviderConfig Cfg = cfg;

    public string Id => Cfg.Id;
    public string Name => string.IsNullOrWhiteSpace(Cfg.Name) ? ProviderMeta.Get(Cfg.Kind).DisplayName : Cfg.Name;
    public ProviderKind Kind => Cfg.Kind;
    public int TimeoutMs => Cfg.TimeoutMs > 0 ? Cfg.TimeoutMs : 6000;
    public virtual bool BatchTargets => false;

    public virtual string? ConfigError
    {
        get
        {
            foreach (var f in ProviderMeta.Get(Kind).Fields)
                if (f.Required && string.IsNullOrWhiteSpace(Opt(f.Key)))
                    return $"请先填写「{f.Label}」";
            return null;
        }
    }

    protected string Opt(string key, string fallback = "")
    {
        var v = Cfg.Options.GetValueOrDefault(key);
        if (!string.IsNullOrWhiteSpace(v)) return v!;
        var def = ProviderMeta.Get(Kind).Fields.FirstOrDefault(f => f.Key == key)?.Default;
        return string.IsNullOrEmpty(def) ? fallback : def!;
    }

    protected TranslateResult New() => new() { ProviderId = Id, ProviderName = Name };

    public async Task<TranslateResult> TranslateAsync(TranslateRequest req, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var r = await DoTranslateAsync(req, ct).ConfigureAwait(false);
            r.ElapsedMs = sw.ElapsedMilliseconds;
            if (r.Texts.Count == 0 && r.Error is null) r.Error = "接口未返回译文";
            return r;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderException ex)
        {
            return TranslateResult.Failed(this, ex.Message, sw.ElapsedMilliseconds);
        }
        catch (NotSupportedException ex)
        {
            return TranslateResult.Failed(this, ex.Message, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return TranslateResult.Failed(this, ex.GetType().Name + "：" + ex.Message, sw.ElapsedMilliseconds);
        }
    }

    protected abstract Task<TranslateResult> DoTranslateAsync(TranslateRequest req, CancellationToken ct);

    /// <summary>语言不支持时统一抛出。</summary>
    protected static NotSupportedException Unsupported(string code)
        => new($"该源不支持{Languages.NameOf(code)}");
}
