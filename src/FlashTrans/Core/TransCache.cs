namespace FlashTrans.Core;

/// <summary>
/// 翻译结果 LRU 缓存，命中时几乎零延迟。
/// 过期条目会自己清掉：写入时摊还清理，另有一个只在缓存非空时武装的后台定时器兜底，
/// 所以放着不动也不会一直占着内存，更不会让过期条目挤掉还新鲜的。
/// </summary>
public sealed class TransCache : IDisposable
{
    readonly record struct Entry(string Text, string? Phonetic, List<DictEntry>? Dict, DateTime At);

    readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Entry>>> _map = new(StringComparer.Ordinal);
    readonly LinkedList<KeyValuePair<string, Entry>> _lru = new();
    readonly object _gate = new();

    /// <summary>后台清理间隔。空闲时会自行解除武装，不白唤醒 CPU。</summary>
    static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    /// <summary>写入多少次顺带清一次过期条目，把全表扫描的成本摊开。</summary>
    const int WritesPerSweep = 64;

    System.Threading.Timer? _timer;
    bool _armed;
    int _writes;
    bool _disposed;

    public TransCache(int capacity, TimeSpan? ttl = null)
    {
        Capacity = capacity;
        _ttl = Normalize(ttl ?? TimeSpan.FromHours(12));
    }

    public int Capacity { get; set; }

    TimeSpan _ttl;

    /// <summary>条目存活时长。改小会让已有条目立刻按新时限判定。</summary>
    public TimeSpan Ttl
    {
        get => _ttl;
        set
        {
            _ttl = Normalize(value);
            Sweep();
        }
    }

    // 下限只为挡住 0 和负值（那会让每条刚写完就算过期，缓存等于废掉）。
    // 面向用户的最小值是 1 小时，由 SettingsService 夹住，这里不必也定那么高。
    static TimeSpan Normalize(TimeSpan ttl) =>
        ttl < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
        : ttl > TimeSpan.FromDays(7) ? TimeSpan.FromDays(7)
        : ttl;

    static string Key(string providerId, string from, string to, string text, bool withDict)
        => string.Concat(providerId, "|", from, "|", to, withDict ? "|d|" : "|-|", text);

    public bool TryGet(string providerId, string from, string to, string text, bool withDict,
                       out string value, out string? phonetic, out List<DictEntry>? dict)
    {
        value = ""; phonetic = null; dict = null;
        if (Capacity <= 0) return false;

        lock (_gate)
        {
            if (!_map.TryGetValue(Key(providerId, from, to, text, withDict), out var node)) return false;
            if (DateTime.UtcNow - node.Value.Value.At > _ttl)
            {
                Drop(node);
                return false;
            }
            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value.Text;
            phonetic = node.Value.Value.Phonetic;
            dict = node.Value.Value.Dict;
            return true;
        }
    }

    public void Set(string providerId, string from, string to, string text,
                    string value, string? phonetic, List<DictEntry>? dict, bool withDict)
    {
        if (Capacity <= 0 || string.IsNullOrEmpty(value)) return;
        var key = Key(providerId, from, to, text, withDict);

        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing)) Drop(existing);

            var node = _lru.AddFirst(new KeyValuePair<string, Entry>(key,
                new Entry(value, phonetic, dict, DateTime.UtcNow)));
            _map[key] = node;

            // 先清过期的，再按容量淘汰：别让过期条目把还新鲜的挤出去
            if (++_writes >= WritesPerSweep)
            {
                _writes = 0;
                SweepLocked();
            }
            Trim();
            Arm();
        }
    }

    /// <summary>清掉某段原文的全部条目（各源、各语向）。「重新翻译」用，不必清空整个缓存。</summary>
    public int InvalidateText(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var suffix = "|" + text;
        var removed = 0;

        lock (_gate)
        {
            var node = _lru.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Key.EndsWith(suffix, StringComparison.Ordinal)) { Drop(node); removed++; }
                node = next;
            }
            if (_map.Count == 0) Disarm();
        }
        return removed;
    }

    /// <summary>清掉所有过期条目，返回清掉的条数。</summary>
    public int Sweep()
    {
        lock (_gate)
        {
            var removed = SweepLocked();
            if (_map.Count == 0) Disarm();
            return removed;
        }
    }

    int SweepLocked()
    {
        // At 是取回译文的时刻，命中不会刷新，所以 LRU 顺序不等于时间顺序，得走全表。
        // 上限就是 Capacity，成本可控。
        var now = DateTime.UtcNow;
        var removed = 0;
        var node = _lru.First;
        while (node is not null)
        {
            var next = node.Next;
            if (now - node.Value.Value.At > _ttl) { Drop(node); removed++; }
            node = next;
        }
        return removed;
    }

    void Trim()
    {
        while (_map.Count > Capacity && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
        }
    }

    void Drop(LinkedListNode<KeyValuePair<string, Entry>> node)
    {
        _lru.Remove(node);
        _map.Remove(node.Value.Key);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
            _writes = 0;
            Disarm();
        }
    }

    public int Count { get { lock (_gate) return _map.Count; } }

    // ---------------------------------------------------------------- 后台清理

    // 都在 _gate 里调用。定时器只在缓存有内容时存在，空了就停，避免空转唤醒。
    void Arm()
    {
        if (_armed || _disposed) return;
        _timer ??= new System.Threading.Timer(_ => Sweep(), null,
            System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
        _timer.Change(SweepInterval, SweepInterval);
        _armed = true;
    }

    void Disarm()
    {
        if (!_armed) return;
        _timer?.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
        _armed = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _armed = false;
            _timer?.Dispose();
            _timer = null;
            _map.Clear();
            _lru.Clear();
        }
    }
}
