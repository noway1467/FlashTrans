using FlashTrans.Services;

namespace FlashTrans.Core;

public sealed class TranslateEngine
{
    public static TranslateEngine Instance { get; } = new();

    public ProviderRegistry Registry { get; } = new();
    public ProviderHealth Health { get; } = new();
    public TransCache Cache { get; }

    AppSettings S => SettingsService.Instance.Current;

    TranslateEngine()
    {
        var s0 = SettingsService.Instance.Current;
        Cache = new TransCache(s0.CacheEnabled ? s0.CacheSize : 0, TimeSpan.FromHours(s0.CacheTtlHours));
        SettingsService.Instance.Changed += s =>
        {
            Registry.Invalidate();
            Cache.Capacity = s.CacheEnabled ? s.CacheSize : 0;
            if (!s.CacheEnabled) Cache.Clear();
            else Cache.Ttl = TimeSpan.FromHours(s.CacheTtlHours);   // setter 顺带清掉按新时限已过期的
        };
    }

    // ---------------------------------------------------------------- 语言解析

    /// <summary>按设置决定实际的源语言与目标语言（含中↔外自动互译）。</summary>
    public (string From, List<string> Targets) Resolve(string text)
    {
        var from = S.SourceLang;
        var targets = S.ResolveTargets();

        if (S.AutoSwapSameLang && !S.MultiTargetEnabled && targets.Count == 1)
        {
            var detected = from == Languages.Auto ? LangDetect.Guess(text) : from;
            if (Same(detected, targets[0]))
            {
                var alt = string.IsNullOrWhiteSpace(S.SecondaryTarget) ? "en" : S.SecondaryTarget;
                if (!Same(detected, alt)) targets = [alt];
            }
        }
        return (from, targets);
    }

    static bool Same(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return LangDetect.IsChinese(a) && LangDetect.IsChinese(b);
    }

    // ---------------------------------------------------------------- 对外入口

    /// <summary>
    /// 聚合模式：并发请求所有已启用的源。
    ///
    /// <paramref name="onStart"/> 在发起请求前同步回调一次，给出空壳批次（已定好源语言和
    /// 目标语言）和参与的源清单；<paramref name="onResult"/> 每有一个源收尾就回调一次。
    /// 界面靠这两个把结果边到边显示——否则要等最慢的源，快的源白等一两秒。
    /// 两个回调都在完成任务的线程上触发，界面自己负责切回 UI 线程。
    /// </summary>
    public async Task<TranslateBatch> AggregateAsync(
        string text, CancellationToken ct,
        Action<TranslateBatch, IReadOnlyList<ProviderConfig>>? onStart = null,
        Action<TranslateResult>? onResult = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (from, targets) = Resolve(text);
        var batch = new TranslateBatch { SourceText = text, From = from, Targets = targets };

        var configs = S.EnabledProviders.ToList();
        if (configs.Count == 0)
        {
            batch.Notes.Add("没有启用任何翻译源，请到设置里开启");
            return batch;
        }

        onStart?.Invoke(batch, configs);

        // 不 using：取消时 WhenAll 会提前抛出，此刻仍有任务在途，
        // 提前 Dispose 会让它们的 Release 抛 ObjectDisposedException 变成未观测异常。
        var gate = new SemaphoreSlim(Math.Max(1, S.MaxParallel));
        var tasks = configs.Select(async cfg =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await RunOneAsync(cfg, text, from, targets, false, ct);
                onResult?.Invoke(r);   // 谁先回来谁先显示，不等别人
                return r;
            }
            finally { gate.Release(); }
        }).ToList();

        // 仍然 WhenAll：要拿齐结果填 batch.Results（复制全部、切标签都要用）。
        // 但界面此时已经把先到的结果画出来了，这里的等待不再是可感知延迟。
        var results = await Task.WhenAll(tasks);
        batch.Results.AddRange(results);
        batch.TotalMs = sw.ElapsedMilliseconds;
        return batch;
    }

    /// <summary>单源模式：失败按顺序自动切换到下一个可用源。</summary>
    public async Task<TranslateBatch> SingleAsync(string providerId, string text, bool perParagraph,
                                                  CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (from, targets) = Resolve(text);
        var batch = new TranslateBatch { SourceText = text, From = from, Targets = targets };

        foreach (var cfg in BuildChain(providerId))
        {
            var r = await RunOneAsync(cfg, text, from, targets, perParagraph, ct);
            batch.Results.Add(r);
            if (r.Ok)
            {
                if (batch.Results.Count > 1)
                    batch.Notes.Add($"已自动切换到「{r.ProviderName}」");
                break;
            }
            if (!S.AutoFallback) break;
            batch.Notes.Add($"{r.ProviderName}：{r.Error}");
        }

        if (batch.Results.Count == 0)
            batch.Notes.Add("没有可用的翻译源，请检查设置");
        batch.TotalMs = sw.ElapsedMilliseconds;
        return batch;
    }

    /// <summary>降级链：首选源在前，随后是其它已启用源（跳过冷却中的）。</summary>
    public List<ProviderConfig> BuildChain(string preferredId)
    {
        var chain = new List<ProviderConfig>();
        var all = S.EnabledProviders.ToList();

        var first = all.FirstOrDefault(p => p.Id == preferredId)
                 ?? S.Find(preferredId)
                 ?? all.FirstOrDefault();
        if (first is not null) chain.Add(first);

        foreach (var p in all)
        {
            if (first is not null && p.Id == first.Id) continue;
            if (Health.IsCoolingDown(p.Id)) continue;
            if (Registry.Get(p).ConfigError is not null) continue;
            chain.Add(p);
        }
        return chain;
    }

    /// <summary>某个源当前是否可用（配置齐全）。</summary>
    public string? ConfigErrorOf(ProviderConfig cfg) => Registry.Get(cfg).ConfigError;

    public ITranslator Impl(ProviderConfig cfg) => Registry.Get(cfg);

    // ---------------------------------------------------------------- 执行

    async Task<TranslateResult> RunOneAsync(ProviderConfig cfg, string text, string from,
                                            List<string> targets, bool perParagraph, CancellationToken ct)
    {
        var impl = Registry.Get(cfg);
        if (impl.ConfigError is { } cfgErr)
            return TranslateResult.Failed(impl, cfgErr);

        var result = new TranslateResult { ProviderId = impl.Id, ProviderName = impl.Name };
        var missing = new List<string>();

        // 带不带词典是两种结果，必须分开存：否则关着词典查过的词，
        // 之后打开词典也只会拿到当初那份没有音标/释义的缓存。
        var wantDict = S.ShowDictionary && LangDetect.LooksLikeWord(text);

        foreach (var tg in targets)
        {
            if (S.CacheEnabled &&
                Cache.TryGet(impl.Id, from, tg, text, wantDict, out var cached, out var ph, out var dict))
            {
                result.Texts[tg] = cached;
                result.Phonetic ??= ph;
                result.Dict ??= dict;
            }
            else missing.Add(tg);
        }
        if (missing.Count == 0)
        {
            result.FromCache = true;
            result.ElapsedMs = 0;
            return result;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var errors = new List<string>();

        if (impl.BatchTargets || missing.Count == 1)
        {
            var r = await CallAsync(impl, text, from, missing, wantDict, perParagraph, ct);
            Merge(result, r, errors);
        }
        else
        {
            var gate = new SemaphoreSlim(Math.Max(1, S.MaxParallel));
            var tasks = missing.Select(async tg =>
            {
                await gate.WaitAsync(ct);
                try { return await CallAsync(impl, text, from, [tg], wantDict, perParagraph, ct); }
                finally { gate.Release(); }
            }).ToList();
            foreach (var r in await Task.WhenAll(tasks)) Merge(result, r, errors);
        }

        result.ElapsedMs = sw.ElapsedMilliseconds;

        if (result.Texts.Count == 0)
        {
            result.Error = errors.Count > 0 ? string.Join("；", errors.Distinct()) : "接口未返回译文";
            Health.Failure(impl.Id, result.Error);
        }
        else
        {
            Health.Success(impl.Id, result.ElapsedMs);
            if (errors.Count > 0) result.Error = null;
            if (S.CacheEnabled)
                foreach (var (lang, value) in result.Texts)
                    Cache.Set(impl.Id, from, lang, text, value, result.Phonetic, result.Dict, wantDict);
        }
        return result;
    }

    static void Merge(TranslateResult into, TranslateResult from, List<string> errors)
    {
        if (from.Error is not null) { errors.Add(from.Error); return; }
        foreach (var (k, v) in from.Texts) into.Texts[k] = v;
        into.Phonetic ??= from.Phonetic;
        into.Dict ??= from.Dict;
        into.DetectedFrom ??= from.DetectedFrom;
    }

    async Task<TranslateResult> CallAsync(ITranslator impl, string text, string from, List<string> targets,
                                          bool wantDict, bool perParagraph, CancellationToken ct)
    {
        var req = new TranslateRequest { Text = text, From = from, Targets = targets, WantDictionary = wantDict };

        // 双语逐段模式：分段并发翻译再拼回，保证行数与原文一致
        if (perParagraph && targets.Count == 1)
        {
            var lines = SplitLines(text);
            if (lines.Count is > 1 and <= 12)
                return await ByParagraphAsync(impl, lines, from, targets[0], ct);
        }
        return await impl.TranslateAsync(req, ct);
    }

    async Task<TranslateResult> ByParagraphAsync(ITranslator impl, List<string> lines, string from,
                                                 string target, CancellationToken ct)
    {
        var gate = new SemaphoreSlim(Math.Max(1, Math.Min(S.MaxParallel, 4)));
        var tasks = lines.Select(async line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return line;
            await gate.WaitAsync(ct);
            try
            {
                var r = await impl.TranslateAsync(
                    new TranslateRequest { Text = line, From = from, Targets = [target] }, ct);
                return r.Get(target) ?? throw new ProviderException(r.Error ?? "分段翻译失败");
            }
            finally { gate.Release(); }
        }).ToList();

        try
        {
            var parts = await Task.WhenAll(tasks);
            var res = new TranslateResult { ProviderId = impl.Id, ProviderName = impl.Name };
            res.Texts[target] = string.Join("\n", parts);
            return res;
        }
        catch (ProviderException ex)
        {
            return TranslateResult.Failed(impl, ex.Message);
        }
    }

    public static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    /// <summary>用于设置页的「测试」按钮。</summary>
    public async Task<TranslateResult> TestAsync(ProviderConfig cfg, CancellationToken ct)
    {
        var impl = Registry.Get(cfg);
        if (impl.ConfigError is { } err) return TranslateResult.Failed(impl, err);
        var target = LangDetect.IsChinese(S.TargetLang) ? S.TargetLang : "zh-CN";
        return await impl.TranslateAsync(new TranslateRequest
        {
            Text = "Hello, world!", From = "en", Targets = [target]
        }, ct);
    }
}
