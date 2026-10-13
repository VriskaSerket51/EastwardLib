using System.Collections;
using System.Text.Json.Nodes;

namespace EastwardLib.MetaData;

public sealed record TextureInfo(JsonObject Data, JsonObject Group)
{
    public string Id { get; init; } = string.Empty;
    public string? AtlasPath => MetadataJson.Text(Group["atlasCachePath"]);
    public bool AtlasMode => Group["atlasMode"]?.ToString() is string mode && mode is not ("false" or "False" or "0" or "");
    public bool PremultiplyAlpha => Group["premultiplyAlpha"]?.ToString() is "true" or "True" or "1";
}

public sealed record TextureGroupInfo(string Id, JsonObject Data);

public class TextureIndex : IReadOnlyDictionary<string, string>
{
    private Dictionary<string, string> _paths = new(StringComparer.Ordinal);
    private Dictionary<string, TextureInfo> _textures = new(StringComparer.Ordinal);
    private List<TextureGroupInfo> _groups = [];
    public JsonObject Data
    {
        get;
    }
    public IReadOnlyDictionary<string, TextureInfo> Textures => _textures;
    public IReadOnlyList<TextureGroupInfo> Groups => _groups;
    public event Action? Changed;
    public string this[string key] => _paths[key];
    public IEnumerable<string> Keys => _paths.Keys;
    public IEnumerable<string> Values => _paths.Values;
    public int Count => _paths.Count;

    private TextureIndex(JsonObject data)
    {
        Data = data;
        Refresh();
    }

    public static TextureIndex Create(string json)
    {
        return new(MetadataJson.Parse(json, "texture index"));
    }

    private static (Dictionary<string, string> Paths, Dictionary<string, TextureInfo> Textures, List<TextureGroupInfo> Groups) Read(JsonObject data)
    {
        var map = MetadataJson.Object(data["map"], "texture map");
        JsonObject Body(string id) => MetadataJson.Object(MetadataJson.Object(map[id], "texture map." + id)["body"], "texture body " + id);
        JsonArray References(JsonObject body, string key)
        {
            if (body[key] == null || body[key] is JsonObject table && table.Count == 0)
            {
                return new JsonArray();
            }
            return body[key] as JsonArray ?? throw new InvalidDataException("Expected references at texture " + key);
        }
        var root = Body(MetadataJson.Reference(data["root"], "texture root"));
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var textures = new Dictionary<string, TextureInfo>(StringComparer.Ordinal);
        var groups = new List<TextureGroupInfo>();
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in References(root, "groups"))
        {
            var id = MetadataJson.Reference(reference, "texture group");
            if (!groupIds.Add(id))
            {
                throw new InvalidDataException("Duplicate texture group: " + id);
            }
            var group = Body(id);
            groups.Add(new(id, group));
            foreach (var textureRef in References(group, "textures"))
            {
                var textureId = MetadataJson.Reference(textureRef, "texture entry");
                var body = Body(textureId);
                var path = MetadataJson.RequiredText(body["path"], "texture path " + textureId);
                var info = new TextureInfo(body, group) { Id = textureId };
                if (!textures.TryAdd(path, info))
                {
                    throw new InvalidDataException("Duplicate texture path: " + path);
                }
                if (info.AtlasMode)
                {
                    paths.Add(path, MetadataJson.RequiredText(group["atlasCachePath"], "atlas path " + id));
                }
            }
        }
        return (paths, textures, groups);
    }

    public void Refresh()
    {
        (_paths, _textures, _groups) = Read(Data);
        Changed?.Invoke();
    }

    /// <summary>Edit an existing serializer body without discarding its envelope or map ID.</summary>
    public void Update(string id, Action<JsonObject> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var candidate = (JsonObject)Data.DeepClone();
        var map = MetadataJson.Object(candidate["map"], "texture map");
        var body = MetadataJson.Object(MetadataJson.Object(map[id], "texture map." + id)["body"], "texture body " + id);
        edit(body);
        Commit(candidate);
    }

    public void RemoveTexture(string path)
    {
        var info = _textures[path];
        var candidate = (JsonObject)Data.DeepClone();
        var map = MetadataJson.Object(candidate["map"], "texture map");
        // Keep the map entry: other serializer objects may still reference this ID.
        DetachTexture(map, info.Id);
        Commit(candidate);
    }

    /// <summary>Add the caller's complete serialized group, including its model and extra fields.</summary>
    public void AddGroup(string id, JsonObject entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(entry);
        var candidate = (JsonObject)Data.DeepClone();
        var map = MetadataJson.Object(candidate["map"], "texture map");
        if (map.ContainsKey(id))
        {
            throw new InvalidDataException("Texture map ID already exists: " + id);
        }
        var rootId = MetadataJson.Reference(candidate["root"], "texture root");
        var root = MetadataJson.Object(MetadataJson.Object(map[rootId], rootId)["body"], rootId);
        map.Add(id, entry.DeepClone());
        if (root["groups"] is not JsonArray)
        {
            root["groups"] = new JsonArray();
        }
        ((JsonArray)root["groups"]!).Add(id);
        Commit(candidate);
    }

    public void RemoveGroup(string id)
    {
        if (!_groups.Any(group => group.Id == id))
        {
            throw new KeyNotFoundException(id);
        }
        var candidate = (JsonObject)Data.DeepClone();
        var map = MetadataJson.Object(candidate["map"], "texture map");
        var rootId = MetadataJson.Reference(candidate["root"], "texture root");
        var root = MetadataJson.Object(MetadataJson.Object(map[rootId], rootId)["body"], rootId);
        var groups = (JsonArray)root["groups"]!;
        for (var index = groups.Count - 1; index >= 0; index--)
        {
            if (MetadataJson.Reference(groups[index], "texture group") == id)
            {
                groups.RemoveAt(index);
            }
        }
        // Retain detached entries because unknown objects can reference them.
        Commit(candidate);
    }

    /// <summary>Add a complete serialized texture under an existing group.</summary>
    public void AddTexture(string groupId, string id, JsonObject entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(entry);
        if (!_groups.Any(group => group.Id == groupId))
        {
            throw new KeyNotFoundException(groupId);
        }
        var candidate = (JsonObject)Data.DeepClone();
        var map = MetadataJson.Object(candidate["map"], "texture map");
        if (map.ContainsKey(id))
        {
            throw new InvalidDataException("Texture map ID already exists: " + id);
        }
        var texture = (JsonObject)entry.DeepClone();
        MetadataJson.Object(texture["body"], "texture body " + id)["parent"] = groupId;
        map.Add(id, texture);
        var group = MetadataJson.Object(MetadataJson.Object(map[groupId], groupId)["body"], groupId);
        if (group["textures"] is not JsonArray)
        {
            group["textures"] = new JsonArray();
        }
        ((JsonArray)group["textures"]!).Add(id);
        Commit(candidate);
    }

    public void MoveTexture(string path, string groupId)
    {
        if (!_groups.Any(group => group.Id == groupId))
        {
            throw new KeyNotFoundException(groupId);
        }
        var info = _textures[path];
        var candidate = (JsonObject)Data.DeepClone();
        var map = MetadataJson.Object(candidate["map"], "texture map");
        DetachTexture(map, info.Id);
        var destination = MetadataJson.Object(MetadataJson.Object(map[groupId], groupId)["body"], groupId);
        if (destination["textures"] is not JsonArray)
        {
            destination["textures"] = new JsonArray();
        }
        ((JsonArray)destination["textures"]!).Add(info.Id);
        MetadataJson.Object(MetadataJson.Object(map[info.Id], info.Id)["body"], info.Id)["parent"] = groupId;
        Commit(candidate);
    }

    private void DetachTexture(JsonObject map, string id)
    {
        foreach (var group in _groups)
        {
            var body = MetadataJson.Object(MetadataJson.Object(map[group.Id], group.Id)["body"], group.Id);
            if (body["textures"] is JsonArray references)
            {
                for (var index = references.Count - 1; index >= 0; index--)
                {
                    if (MetadataJson.Reference(references[index], "texture reference") == id)
                    {
                        references.RemoveAt(index);
                    }
                }
            }
        }
    }

    private void Commit(JsonObject candidate)
    {
        Read(candidate);
        MetadataJson.Replace(Data, candidate);
        Refresh();
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

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        return _paths.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
