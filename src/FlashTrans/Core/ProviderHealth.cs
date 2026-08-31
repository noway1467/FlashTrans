using System.Collections.Concurrent;

namespace FlashTrans.Core;

/// <summary>记录各源的成败，连续失败进入冷却，自动切换时跳过。</summary>
public sealed class ProviderHealth
{
    sealed class State
    {
        public int Fails;
        public DateTime CooldownUntil;
        public long LastMs;
        public bool LastOk = true;
        public string? LastError;
    }

    readonly ConcurrentDictionary<string, State> _map = new(StringComparer.Ordinal);
    const int FailThreshold = 2;
    static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    public void Success(string id, long ms)
    {
        var s = _map.GetOrAdd(id, _ => new State());
        lock (s)
        {
            s.Fails = 0;
            s.CooldownUntil = default;
            s.LastMs = ms;
            s.LastOk = true;
            s.LastError = null;
        }
    }

    public void Failure(string id, string error)
    {
        var s = _map.GetOrAdd(id, _ => new State());
        // 聚合模式下多个源并发收尾，Fails++ 不能裸奔，否则连续失败会被少数一次、迟迟进不了冷却
        lock (s)
        {
            s.Fails++;
            s.LastOk = false;
            s.LastError = error;
            if (s.Fails >= FailThreshold) s.CooldownUntil = DateTime.UtcNow + Cooldown;
        }
    }

    /// <summary>是否处于冷却（自动降级时应跳过）。</summary>
    public bool IsCoolingDown(string id) =>
        _map.TryGetValue(id, out var s) && s.CooldownUntil > DateTime.UtcNow;

    public int SecondsLeft(string id) =>
        _map.TryGetValue(id, out var s) && s.CooldownUntil > DateTime.UtcNow
            ? (int)Math.Ceiling((s.CooldownUntil - DateTime.UtcNow).TotalSeconds) : 0;

    public bool LastOk(string id) => !_map.TryGetValue(id, out var s) || s.LastOk;
    public long LastMs(string id) => _map.TryGetValue(id, out var s) ? s.LastMs : 0;
    public string? LastError(string id) => _map.TryGetValue(id, out var s) ? s.LastError : null;

    public void Reset(string? id = null)
    {
        if (id is null) _map.Clear();
        else _map.TryRemove(id, out _);
    }
}
