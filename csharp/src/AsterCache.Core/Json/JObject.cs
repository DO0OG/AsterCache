using System.Collections;

namespace AsterCache.Core.Json;

/// 삽입 순서를 지키는 JSON 객체 (Python dict). 같은 키를 다시 넣으면 값만 바뀌고 위치는 유지됩니다.
public sealed class JObject : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly List<KeyValuePair<string, object?>> _items = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public int Count => _items.Count;

    public object? this[string key]
    {
        get => _items[_index[key]].Value;
        set => Add(key, value);
    }

    public void Add(string key, object? value)
    {
        if (_index.TryGetValue(key, out int i))
        {
            _items[i] = new KeyValuePair<string, object?>(key, value);
            return;
        }

        _index[key] = _items.Count;
        _items.Add(new KeyValuePair<string, object?>(key, value));
    }

    public bool ContainsKey(string key) => _index.ContainsKey(key);

    public bool TryGetValue(string key, out object? value)
    {
        if (_index.TryGetValue(key, out int i))
        {
            value = _items[i].Value;
            return true;
        }

        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
