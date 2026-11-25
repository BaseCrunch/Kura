namespace Kura.Core.Util;

/// <summary>Thread-safe LRU cache bounded by a total cost (e.g. decoded bytes) rather than an item count.</summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _list = new();
    private readonly Func<TValue, long> _cost;
    private readonly Action<TValue>? _onEvict;
    private readonly object _gate = new();
    private long _total;

    public LruCache(long capacity, Func<TValue, long> cost, Action<TValue>? onEvict = null, IEqualityComparer<TKey>? comparer = null)
    {
        Capacity = capacity;
        _cost = cost;
        _onEvict = onEvict;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    public long Capacity { get; set; }
    public long TotalCost { get { lock (_gate) return _total; } }
    public int Count { get { lock (_gate) return _map.Count; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        var cost = Math.Max(1, _cost(value));
        List<TValue>? evicted = null;
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _list.Remove(existing);
                _total -= existing.Value.Cost;
                _map.Remove(key);
                (evicted ??= []).Add(existing.Value.Value);
            }
            var node = _list.AddFirst(new Entry(key, value, cost));
            _map[key] = node;
            _total += cost;
            // Always keep the newest item even if it alone exceeds capacity.
            while (_total > Capacity && _list.Count > 1)
            {
                var last = _list.Last!;
                _list.RemoveLast();
                _map.Remove(last.Value.Key);
                _total -= last.Value.Cost;
                (evicted ??= []).Add(last.Value.Value);
            }
        }
        if (evicted is not null && _onEvict is not null)
            foreach (var v in evicted) if (!ReferenceEquals(v, value)) _onEvict(v);
    }

    public bool Remove(TKey key)
    {
        TValue? removed = default;
        bool found;
        lock (_gate)
        {
            found = _map.Remove(key, out var node);
            if (found)
            {
                _list.Remove(node!);
                _total -= node!.Value.Cost;
                removed = node.Value.Value;
            }
        }
        if (found && _onEvict is not null) _onEvict(removed!);
        return found;
    }

    public void Clear()
    {
        List<TValue> all;
        lock (_gate)
        {
            all = _list.Select(e => e.Value).ToList();
            _list.Clear();
            _map.Clear();
            _total = 0;
        }
        if (_onEvict is not null) foreach (var v in all) _onEvict(v);
    }

    private readonly record struct Entry(TKey Key, TValue Value, long Cost);
}
