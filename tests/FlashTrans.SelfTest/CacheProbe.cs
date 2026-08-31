using FlashTrans.Core;

namespace FlashTrans.SelfTest;

/// <summary>缓存不联网也能测：过期自清、按容量淘汰、词典分开存、按原文失效。</summary>
static class CacheProbe
{
    public static void RunAll(Action<string, Action> step)
    {
        step("缓存：命中与未命中", HitMiss);
        step("缓存：过期条目自动清掉", ExpiryOnWrite);
        step("缓存：淘汰顺序（过期优先，其次最久未用）", EvictionOrder);
        step("缓存：带词典与不带词典分开存", DictKeyed);
        step("缓存：按原文失效只清这一段", InvalidateOne);
        step("缓存：容量为 0 时不存", Disabled);
        step("缓存：改短保留时长立刻生效", ShrinkTtl);
        step("缓存：后台定时器空闲时自行解除", TimerDisarms);
    }

    static TransCache New(int cap = 8, int ttlMinutes = 60)
        => new(cap, TimeSpan.FromMinutes(ttlMinutes));

    static void Put(TransCache c, string text, string value, bool dict = false)
        => c.Set("p", "en", "zh-CN", text, value, null, null, dict);

    static bool Got(TransCache c, string text, bool dict = false)
        => c.TryGet("p", "en", "zh-CN", text, dict, out _, out _, out _);

    static void HitMiss()
    {
        using var c = New();
        Put(c, "hello", "你好");
        if (!c.TryGet("p", "en", "zh-CN", "hello", false, out var v, out _, out _))
            throw new InvalidOperationException("刚写进去就没命中");
        if (v != "你好") throw new InvalidOperationException("取回的译文不对：" + v);
        if (Got(c, "world")) throw new InvalidOperationException("没存过的文本也命中了");
        // 换语向、换源都该是另一条
        if (c.TryGet("p", "en", "ja", "hello", false, out _, out _, out _))
            throw new InvalidOperationException("语向没进 key");
        if (c.TryGet("q", "en", "zh-CN", "hello", false, out _, out _, out _))
            throw new InvalidOperationException("源 id 没进 key");
    }

    /// <summary>用户抱怨的就是这个：过期了还占着地方。等它真的过期，看还在不在。</summary>
    static void ExpiryOnWrite()
    {
        using var c = New(cap: 200, ttlMinutes: 60);
        for (int i = 0; i < 3; i++) Put(c, "old" + i, "旧" + i);

        var removed = c.Sweep();
        if (removed != 0) throw new InvalidOperationException("没过期却被清掉了 " + removed + " 条");
        if (c.Count != 3) throw new InvalidOperationException("Sweep 动了不该动的条目");

        // 真的等到过期
        c.Ttl = TimeSpan.FromSeconds(1);
        Thread.Sleep(1200);
        if (Got(c, "old0")) throw new InvalidOperationException("过期条目还能命中");

        // 命中时顺带清掉了 old0，剩下两条要靠 Sweep 自己清，不能等下次查到它才清
        removed = c.Sweep();
        if (removed != 2) throw new InvalidOperationException($"Sweep 该清掉剩下 2 条，实际 {removed}");
        if (c.Count != 0) throw new InvalidOperationException("过期条目没清干净，剩 " + c.Count);
    }

    /// <summary>容量满时先淘汰过期的，新鲜的留下；同样新鲜时按最久未用淘汰。</summary>
    static void EvictionOrder()
    {
        using var stale = New(cap: 4, ttlMinutes: 60);
        stale.Ttl = TimeSpan.FromSeconds(1);
        for (int i = 0; i < 3; i++) Put(stale, "stale" + i, "旧" + i);
        Thread.Sleep(1200);
        Put(stale, "fresh", "新");
        Put(stale, "newest", "最新");     // 第 5 条，触发容量淘汰

        if (!Got(stale, "fresh")) throw new InvalidOperationException("新鲜的 fresh 被挤掉了");
        if (!Got(stale, "newest")) throw new InvalidOperationException("最新写的不在");
        if (Got(stale, "stale0")) throw new InvalidOperationException("过期的 stale0 还能命中");

        using var c = New(cap: 3);
        Put(c, "a", "甲");
        Put(c, "b", "乙");
        Put(c, "c", "丙");
        _ = Got(c, "a");          // a 提到最前，b 成了最久未用
        Put(c, "d", "丁");        // 触发淘汰

        if (c.Count != 3) throw new InvalidOperationException("容量没守住：" + c.Count);
        if (!Got(c, "a")) throw new InvalidOperationException("刚命中过的 a 被淘汰了");
        if (Got(c, "b")) throw new InvalidOperationException("最久未用的 b 该被淘汰");
        if (!Got(c, "d")) throw new InvalidOperationException("新写的 d 不在");
    }

    /// <summary>关着词典查过的词，打开词典后不该拿到那份没有音标的缓存。</summary>
    static void DictKeyed()
    {
        using var c = New();
        c.Set("p", "en", "zh-CN", "hello", "你好", null, null, withDict: false);
        if (Got(c, "hello", dict: true))
            throw new InvalidOperationException("不带词典的缓存被当成带词典的用了");

        c.Set("p", "en", "zh-CN", "hello", "你好",
              "həˈləʊ", [new DictEntry { Pos = "int.", Terms = ["你好"] }], withDict: true);
        if (!c.TryGet("p", "en", "zh-CN", "hello", true, out _, out var ph, out var dict))
            throw new InvalidOperationException("带词典的那条没存下");
        if (ph != "həˈləʊ" || dict is null || dict.Count == 0)
            throw new InvalidOperationException("音标或释义没跟着回来");

        // 两条并存，互不影响
        if (!Got(c, "hello")) throw new InvalidOperationException("不带词典的那条被覆盖了");
        if (c.Count != 2) throw new InvalidOperationException("应该是两条独立条目，实际 " + c.Count);
    }

    static void InvalidateOne()
    {
        using var c = New(cap: 50);
        c.Set("p", "en", "zh-CN", "hello", "你好", null, null, false);
        c.Set("q", "en", "zh-CN", "hello", "您好", null, null, false);
        c.Set("p", "en", "ja", "hello", "こんにちは", null, null, false);
        c.Set("p", "en", "zh-CN", "hello", "你好（词典）", null, null, true);
        Put(c, "keep me", "留着");

        var n = c.InvalidateText("hello");
        if (n != 4) throw new InvalidOperationException($"该清掉 4 条（各源各语向），实际 {n}");
        if (Got(c, "hello")) throw new InvalidOperationException("hello 还在");
        if (!Got(c, "keep me")) throw new InvalidOperationException("把别的文本一起清掉了");

        // 前缀相同但不是同一段原文，不该被误清
        using var c2 = New();
        Put(c2, "hello", "你好");
        Put(c2, "hello world", "你好世界");
        c2.InvalidateText("world");
        if (!Got(c2, "hello world")) throw new InvalidOperationException("误清了 hello world");
    }

    static void Disabled()
    {
        using var c = New(cap: 0);
        Put(c, "hello", "你好");
        if (c.Count != 0) throw new InvalidOperationException("容量 0 还存进去了");
        if (Got(c, "hello")) throw new InvalidOperationException("容量 0 还能命中");

        // 空译文不该占位
        using var c2 = New();
        Put(c2, "hello", "");
        if (c2.Count != 0) throw new InvalidOperationException("空译文被存下了");
    }

    static void ShrinkTtl()
    {
        using var c = New(cap: 20, ttlMinutes: 120);
        Put(c, "hello", "你好");
        c.Ttl = TimeSpan.FromMinutes(1);      // setter 顺带扫一遍
        if (c.Ttl != TimeSpan.FromMinutes(1)) throw new InvalidOperationException("Ttl 没生效");
        if (!Got(c, "hello")) throw new InvalidOperationException("刚写的条目被 1 分钟时限判过期了");

        // 越界值被夹回合法区间，不该出现 0 或负的时限把缓存彻底废掉
        c.Ttl = TimeSpan.Zero;
        if (c.Ttl < TimeSpan.FromSeconds(1)) throw new InvalidOperationException("下限没夹住：" + c.Ttl);
        c.Ttl = TimeSpan.FromMinutes(-5);
        if (c.Ttl < TimeSpan.FromSeconds(1)) throw new InvalidOperationException("负值没夹住：" + c.Ttl);
        Put(c, "still works", "还能用");
        if (!Got(c, "still works")) throw new InvalidOperationException("夹完时限后缓存不工作了");
        c.Ttl = TimeSpan.FromDays(3650);
        if (c.Ttl > TimeSpan.FromDays(7)) throw new InvalidOperationException("上限没夹住：" + c.Ttl);
    }

    /// <summary>Clear / InvalidateText 把缓存清空后，定时器应该停掉，空闲时不白唤醒。</summary>
    static void TimerDisarms()
    {
        var c = New();
        Put(c, "hello", "你好");
        c.Clear();
        if (c.Count != 0) throw new InvalidOperationException("Clear 没清空");

        // 清空后再写再清，反复几轮不该抛（定时器要能重新武装）
        for (int i = 0; i < 3; i++)
        {
            Put(c, "x" + i, "值" + i);
            if (c.InvalidateText("x" + i) != 1) throw new InvalidOperationException("按原文失效失败");
        }

        c.Dispose();
        // Dispose 之后再用不该炸：关窗顺序不保证，翻译可能刚好在收尾
        Put(c, "after", "之后");
        _ = Got(c, "after");
        _ = c.Sweep();
        c.Dispose();
    }
}
