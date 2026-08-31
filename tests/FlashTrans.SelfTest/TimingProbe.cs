using System.Diagnostics;
using FlashTrans.Core;
using FlashTrans.Services;

namespace FlashTrans.SelfTest;

/// <summary>
/// 用用户的真实配置跑一遍聚合翻译，把每个源的耗时打出来。
/// 聚合模式是 WhenAll，整体时间等于最慢那个源，所以要看清是谁在拖。
/// </summary>
static class TimingProbe
{
    const string Sample = "The quick brown fox jumps over the lazy dog.";

    public static void Run()
    {
        var s = SettingsService.Instance.Current;
        var engine = TranslateEngine.Instance;

        Console.WriteLine($"\n启用的源：{s.EnabledProviders.Count()} 个 · 并发上限 {s.MaxParallel} " +
                          $"· 打字防抖 {s.TypeDelayMs}ms · 缓存 {(s.CacheEnabled ? "开" : "关")}");

        // 冷/热各跑一轮：第一轮含 DNS+TLS，第二轮走池化连接。
        for (var round = 1; round <= 2; round++)
        {
            engine.Cache.Clear();   // 不清缓存第二轮会 0ms，测不到网络
            var sw = Stopwatch.StartNew();
            long firstAt = -1;
            var arrivals = new List<string>();
            var batch = engine.AggregateAsync(Sample, CancellationToken.None,
                onStart: (_, cfgs) => arrivals.Add($"占位卡 {cfgs.Count} 张 @{sw.ElapsedMilliseconds}ms"),
                onResult: r =>
                {
                    lock (arrivals)
                    {
                        if (firstAt < 0) firstAt = sw.ElapsedMilliseconds;
                        arrivals.Add($"{r.ProviderName} 到达 @{sw.ElapsedMilliseconds}ms");
                    }
                }).GetAwaiter().GetResult();
            sw.Stop();

            Console.WriteLine($"\n[边到边] 首个结果 @{firstAt}ms，全部齐 @{sw.ElapsedMilliseconds}ms " +
                              $"→ 提前 {sw.ElapsedMilliseconds - firstAt}ms 见到译文");
            foreach (var a in arrivals) Console.WriteLine("    " + a);

            Console.WriteLine($"\n--- 第 {round} 轮（{(round == 1 ? "冷" : "热")}）总耗时 {sw.ElapsedMilliseconds}ms ---");
            foreach (var r in batch.Results.OrderByDescending(x => x.ElapsedMs))
            {
                var bar = new string('#', (int)Math.Min(40, r.ElapsedMs / 50));
                var state = r.Ok ? "ok  " : "失败";
                Console.WriteLine($"  {r.ElapsedMs,6}ms {state} {r.ProviderName,-16} {bar}");
                if (!r.Ok && !string.IsNullOrWhiteSpace(r.Error))
                    Console.WriteLine($"          └ {r.Error}");
            }

            var slowest = batch.Results.OrderByDescending(x => x.ElapsedMs).FirstOrDefault();
            var fastest = batch.Results.Where(x => x.Ok).OrderBy(x => x.ElapsedMs).FirstOrDefault();
            if (slowest is not null && fastest is not null)
                Console.WriteLine($"  最快 {fastest.ProviderName} {fastest.ElapsedMs}ms · " +
                                  $"最慢 {slowest.ProviderName} {slowest.ElapsedMs}ms · " +
                                  $"被拖慢 {slowest.ElapsedMs - fastest.ElapsedMs}ms");
        }

        // 缓存命中该是零延迟
        var sw3 = Stopwatch.StartNew();
        engine.AggregateAsync(Sample, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"\n第 3 轮（缓存命中）总耗时 {sw3.ElapsedMilliseconds}ms");
    }
}
