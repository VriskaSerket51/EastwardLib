using System.Text.Json.Nodes;

namespace EastwardLib.MetaData;

public sealed class AssetInfo
{
    public string AssetName
    {
        get; internal set;
    }
    public JsonObject Data
    {
        get;
    }
    public MetadataPaths ObjectFiles
    {
        get;
    }
    internal event Action? Changed;

    public string? FilePath
    {
        get => MetadataJson.Text(Data["filePath"]);
        set => Set("filePath", value == null ? JsonValue.Create(false) : JsonValue.Create(value));
    }

    public string FileType
    {
        get => MetadataJson.Text(Data["fileType"]) ?? string.Empty;
        set => Set("fileType", JsonValue.Create(value));
    }

    public string? GroupType
    {
        get => MetadataJson.Text(Data["groupType"]);
        set => Set("groupType", JsonValue.Create(value));
    }

    public string Type
    {
        get => MetadataJson.Text(Data["type"]) ?? string.Empty;
        set => Set("type", JsonValue.Create(value));
    }

    public string Parent
    {
        get => MetadataJson.Text(Data["parent"]) ?? (AssetName.Contains('/') ? AssetName[..AssetName.LastIndexOf('/')] : string.Empty);
        set => Set("parent", JsonValue.Create(value));
    }

    public JsonObject? Properties
    {
        get => Data["properties"] as JsonObject;
        set => Set("properties", value);
    }

    public JsonObject? DeployMeta
    {
        get => Data["deployMeta"] as JsonObject;
        set => Set("deployMeta", value);
    }

    public JsonArray? Tags
    {
        get => Data["tags"] as JsonArray;
        set => Set("tags", value);
    }

    public JsonObject? Dependency
    {
        get => Data["dependency"] as JsonObject;
        set => Set("dependency", value);
    }

    public JsonNode? FileTime
    {
        get => Data["fileTime"];
        set => Set("fileTime", value);
    }

    public JsonNode? ManagerVersion
    {
        get => Data["managerVersion"];
        set => Set("managerVersion", value);
    }

    public string Name => AssetName[(AssetName.LastIndexOf('/') + 1)..];

    internal AssetInfo(string assetName, JsonObject data)
    {
        AssetName = assetName;
        Data = data;
        ObjectFiles = new MetadataPaths(() => Data["objectFiles"] as JsonObject,
            files => Data["objectFiles"] = files, () => Changed?.Invoke());
    }

    public AssetInfo(string assetName, string? filePath, string fileType, string? groupType, Dictionary<string, string> objectFiles, string type)
        : this(assetName, new JsonObject
        {
            ["filePath"] = filePath == null ? JsonValue.Create(false) : JsonValue.Create(filePath),
            ["fileType"] = fileType,
            ["groupType"] = groupType,
            ["type"] = type,
            ["objectFiles"] = new JsonObject(objectFiles.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value))))
        })
    {
    }

    private void Set(string key, JsonNode? value)
    {
        var present = Data.TryGetPropertyValue(key, out var previous);
        Data[key] = value?.DeepClone();
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            if (present)
            {
                Data[key] = previous;
            }
            else
            {
                Data.Remove(key);
            }
            throw;
        }
    }
}
