using System.Collections;
using System.Text.Json.Nodes;

namespace EastwardLib.MetaData;

/// <summary>A string view of JSON paths; other values remain in the source document.</summary>
public sealed class MetadataPaths : IDictionary<string, string>, IReadOnlyDictionary<string, string>
{
    private readonly Func<JsonObject?> _data;
    private readonly Action<JsonObject> _set;
    private readonly Action _changed;

    internal MetadataPaths(Func<JsonObject?> data, Action<JsonObject> set, Action changed)
    {
        _data = data;
        _set = set;
        _changed = changed;
    }

    public string this[string key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            WritableData()[key] = value;
            _changed();
        }
    }

    public ICollection<string> Keys => this.Select(pair => pair.Key).ToArray();
    public ICollection<string> Values => this.Select(pair => pair.Value).ToArray();
    IEnumerable<string> IReadOnlyDictionary<string, string>.Keys => Keys;
    IEnumerable<string> IReadOnlyDictionary<string, string>.Values => Values;
    public int Count => ReadData.Count(pair => MetadataJson.Text(pair.Value) != null);
    public bool IsReadOnly => false;

    public void Add(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WritableData().Add(key, JsonValue.Create(value));
        _changed();
    }

    public bool ContainsKey(string key)
    {
        return TryGetValue(key, out _);
    }

    public bool TryGetValue(string key, out string value)
    {
        var text = MetadataJson.Text(ReadData[key]);
        value = text ?? string.Empty;
        return text != null;
    }

    public string? GetValueOrDefault(string key)
    {
        return TryGetValue(key, out var value) ? value : null;
    }

    public bool Remove(string key)
    {
        var removed = _data()?.Remove(key) == true;
        if (removed)
        {
            _changed();
        }
        return removed;
    }

    public void Clear()
    {
        _data()?.Clear();
        _changed();
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        foreach (var (key, node) in ReadData)
        {
            if (MetadataJson.Text(node) is string value)
            {
                yield return new(key, value);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Add(KeyValuePair<string, string> item)
    {
        Add(item.Key, item.Value);
    }

    public bool Contains(KeyValuePair<string, string> item)
    {
        return TryGetValue(item.Key, out var value) && value == item.Value;
    }

    public void CopyTo(KeyValuePair<string, string>[] array, int arrayIndex)
    {
        this.ToArray().CopyTo(array, arrayIndex);
    }

    public bool Remove(KeyValuePair<string, string> item)
    {
        return Contains(item) && Remove(item.Key);
    }

    private JsonObject ReadData => _data() ?? new JsonObject();

    private JsonObject WritableData()
    {
        var data = _data();
        if (data == null)
        {
            data = new JsonObject();
            _set(data);
        }
        return data;
    }
}
