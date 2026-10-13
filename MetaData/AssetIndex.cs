using System.Collections;
using System.Text;
using System.Text.Json.Nodes;
using EastwardLib.Utils;
using MessagePack;

namespace EastwardLib.MetaData;

public class AssetIndex : IReadOnlyDictionary<string, AssetInfo>
{
    private Dictionary<string, AssetInfo> _assets = new(StringComparer.Ordinal);
    public JsonObject Data
    {
        get;
    }
    public event Action? Changed;
    public AssetInfo this[string key] => _assets[key];
    public IEnumerable<string> Keys => _assets.Keys;
    public IEnumerable<AssetInfo> Values => _assets.Values;
    public int Count => _assets.Count;

    private AssetIndex(JsonObject data)
    {
        Data = data;
        Refresh();
    }

    public static AssetIndex Create(string json)
    {
        return new(MetadataJson.Parse(json, "asset index"));
    }

    public static AssetIndex FromPacked(byte[] data)
    {
        try
        {
            return Create(MessagePackSerializer.ConvertToJson(data));
        }
        catch (MessagePackSerializationException error)
        {
            throw new InvalidDataException("Invalid packed asset index", error);
        }
    }

    public void Refresh()
    {
        var assets = new Dictionary<string, AssetInfo>(StringComparer.Ordinal);
        foreach (var (name, node) in Data)
        {
            ValidateName(name);
            var data = MetadataJson.Object(node, "asset " + name);
            Validate(data, name);
            var info = new AssetInfo(name, data);
            info.Changed += OnChanged;
            assets.Add(name, info);
        }
        ValidateParents(assets);
        foreach (var info in _assets.Values)
        {
            info.Changed -= OnChanged;
        }
        _assets = assets;
        OnChanged();
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("Invalid asset name: " + name);
        }
    }

    private static void Validate(JsonObject data, string name)
    {
        foreach (var key in new[] { "type", "fileType", "groupType", "parent" })
        {
            if (data[key] != null && MetadataJson.Text(data[key]) == null)
            {
                throw new InvalidDataException("Expected a string at " + name + "." + key);
            }
        }
        if (data["filePath"] is JsonNode path && MetadataJson.Text(path) == null && path.ToJsonString() != "false")
        {
            throw new InvalidDataException("Expected a source path or false at " + name + ".filePath");
        }
        foreach (var key in new[] { "objectFiles", "properties", "deployMeta", "dependency" })
        {
            if (data[key] is JsonNode value && value is not JsonObject && !(value is JsonArray array && array.Count == 0))
            {
                throw new InvalidDataException("Expected a table at " + name + "." + key);
            }
        }
        if (data["tags"] is JsonNode tags && (tags is not JsonArray tagArray || tagArray.Any(tag => MetadataJson.Text(tag) == null)))
        {
            throw new InvalidDataException("Expected string tags at " + name);
        }
    }

    public void Add(string name, AssetInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ValidateName(name);
        Validate(info.Data, name);
        var candidate = (JsonObject)Data.DeepClone();
        candidate.Add(name, info.Data.DeepClone());
        Commit(candidate);
    }

    public bool Remove(string name)
    {
        var candidate = (JsonObject)Data.DeepClone();
        var removed = candidate.Remove(name);
        if (removed)
        {
            Commit(candidate);
        }
        return removed;
    }

    public void Rename(string name, string replacement)
    {
        ValidateName(replacement);
        if (!_assets.ContainsKey(name))
        {
            throw new KeyNotFoundException(name);
        }
        if (name == replacement)
        {
            return;
        }
        var candidate = (JsonObject)Data.DeepClone();
        var names = _assets.Keys.Where(key => key == name || key.StartsWith(name + "/", StringComparison.Ordinal)).ToArray();
        var moved = names.ToHashSet(StringComparer.Ordinal);
        if (names.Any(key => Data.ContainsKey(replacement + key[name.Length..]) && !moved.Contains(replacement + key[name.Length..])))
        {
            throw new InvalidDataException("Asset rename collides with an existing entry");
        }
        var rows = names.Select(key => (Name: replacement + key[name.Length..], Data: Data[key]!.DeepClone())).ToArray();
        foreach (var key in names)
        {
            candidate.Remove(key);
        }
        foreach (var row in rows)
        {
            candidate.Add(row.Name, row.Data);
        }
        foreach (var row in candidate.Select(pair => pair.Value).OfType<JsonObject>())
        {
            if (MetadataJson.Text(row["parent"]) is string parent && (parent == name || parent.StartsWith(name + "/", StringComparison.Ordinal)))
            {
                row["parent"] = replacement + parent[name.Length..];
            }
        }
        Commit(candidate);
    }

    private void Commit(JsonObject candidate)
    {
        new AssetIndex(candidate);
        MetadataJson.Replace(Data, candidate);
        Refresh();
    }

    public string ToJson(bool indented = true)
    {
        var assets = new Dictionary<string, AssetInfo>(StringComparer.Ordinal);
        foreach (var (name, node) in Data)
        {
            ValidateName(name);
            Validate(MetadataJson.Object(node, name), name);
            assets.Add(name, new AssetInfo(name, (JsonObject)node!));
        }
        ValidateParents(assets);
        return MetadataJson.Serialize(Data, indented);
    }

    public void ValidateRelationships()
    {
        ValidateParents(_assets);
    }

    private static void ValidateParents(IReadOnlyDictionary<string, AssetInfo> assets)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in assets.Keys)
        {
            if (visited.Contains(name))
            {
                continue;
            }
            var branch = new HashSet<string>(StringComparer.Ordinal);
            var parent = name;
            while (assets.TryGetValue(parent, out var info) && !visited.Contains(parent))
            {
                if (!branch.Add(parent))
                {
                    throw new InvalidDataException("Cyclic asset parent: " + parent);
                }
                parent = info.Parent;
            }
            visited.UnionWith(branch);
        }
    }

    public byte[] Encode(bool packed = false)
    {
        var json = ToJson(false);
        return packed ? MessagePackSerializer.ConvertFromJson(json) : Encoding.UTF8.GetBytes(json);
    }

    public void SaveTo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var data = Encode(path.EndsWith(".packed", StringComparison.OrdinalIgnoreCase));
        File.WriteAllBytes(SafePath.PrepareFile(path), data);
    }
    public bool ContainsKey(string key)
    {
        return _assets.ContainsKey(key);
    }

    public bool TryGetValue(string key, out AssetInfo value)
    {
        return _assets.TryGetValue(key, out value!);
    }

    public AssetInfo? SearchAssetInfo(string key)
    {
        return _assets.GetValueOrDefault(key);
    }

    public AssetInfo? GetAssetInfoByName(string name)
    {
        return SearchAssetInfo(name);
    }

    internal static string? Text(JsonNode? node)
    {
        return MetadataJson.Text(node);
    }

    private void OnChanged()
    {
        Changed?.Invoke();
    }

    public IEnumerator<KeyValuePair<string, AssetInfo>> GetEnumerator()
    {
        return _assets.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
