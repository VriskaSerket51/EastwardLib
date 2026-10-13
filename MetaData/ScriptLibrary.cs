using System.Collections;
using System.Text.Json.Nodes;

namespace EastwardLib.MetaData;

public class ScriptLibrary : IReadOnlyDictionary<string, string>
{
    private Dictionary<string, string> _paths = new(StringComparer.Ordinal);
    public JsonObject Data
    {
        get;
    }
    public event Action? Changed;
    public IEnumerable<string> Keys => _paths.Keys;
    public IEnumerable<string> Values => _paths.Values;
    public int Count => _paths.Count;

    public string this[string source]
    {
        get => _paths[source];
        set
        {
            var candidate = (JsonObject)Data.DeepClone();
            var sources = MetadataJson.Object(candidate["source"], "script source");
            var exports = MetadataJson.Object(candidate["export"], "script export");
            var modules = sources.Where(pair => MetadataJson.Text(pair.Value) == source).Select(pair => pair.Key).ToArray();
            if (modules.Length == 0)
            {
                throw new KeyNotFoundException(source);
            }
            foreach (var module in modules)
            {
                exports[module] = value;
            }
            Commit(candidate);
        }
    }

    private ScriptLibrary(JsonObject data)
    {
        Data = data;
        Refresh();
    }

    public static ScriptLibrary Create(string json)
    {
        return new(MetadataJson.Parse(json, "script library"));
    }

    private static Dictionary<string, string> Read(JsonObject data)
    {
        var sources = data["source"] == null ? new JsonObject() : MetadataJson.Object(data["source"], "script source");
        var exports = data["export"] == null ? new JsonObject() : MetadataJson.Object(data["export"], "script export");
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (module, node) in sources)
        {
            var source = MetadataJson.RequiredText(node, "source." + module);
            var export = MetadataJson.RequiredText(exports[module], "export." + module);
            if (paths.TryGetValue(source, out var previous) && previous != export)
            {
                throw new InvalidDataException("Conflicting script exports for " + source);
            }
            paths[source] = export;
        }
        if (exports.Any(pair => !sources.ContainsKey(pair.Key)))
        {
            throw new InvalidDataException("Script export has no matching source module");
        }
        return paths;
    }

    public void Refresh()
    {
        _paths = Read(Data);
        Changed?.Invoke();
    }

    public void SetModule(string module, string source, string export)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        var candidate = (JsonObject)Data.DeepClone();
        candidate["source"] ??= new JsonObject();
        candidate["export"] ??= new JsonObject();
        MetadataJson.Object(candidate["source"], "script source")[module] = source;
        MetadataJson.Object(candidate["export"], "script export")[module] = export;
        Commit(candidate);
    }

    public bool RemoveModule(string module)
    {
        var candidate = (JsonObject)Data.DeepClone();
        var removed = (candidate["source"] as JsonObject)?.Remove(module) == true;
        if (removed)
        {
            (candidate["export"] as JsonObject)?.Remove(module);
            Commit(candidate);
        }
        return removed;
    }

    private void Commit(JsonObject candidate)
    {
        var paths = Read(candidate);
        MetadataJson.Replace(Data, candidate);
        _paths = paths;
        Changed?.Invoke();
    }

    public string ToJson(bool indented = true)
    {
        Read(Data);
        return MetadataJson.Serialize(Data, indented);
    }

    public void SaveTo(string path)
    {
        MetadataJson.Save(path, ToJson());
    }

    public bool ContainsKey(string key)
    {
        return _paths.ContainsKey(key);
    }

    public bool TryGetValue(string key, out string value)
    {
        return _paths.TryGetValue(key, out value!);
    }

    public string? GetValueOrDefault(string key)
    {
        return _paths.GetValueOrDefault(key);
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        return _paths.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
