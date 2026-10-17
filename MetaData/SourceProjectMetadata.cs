using System.Text.Json;
using EastwardLib.Utils;

namespace EastwardLib.MetaData;

public sealed class SourceProjectMetadata
{
    public const string FileName = ".eastward-project.json";
    public string Format => "eastward-source-project";
    public int Version => 1;
    public Dictionary<string, string> Assets { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PathRemappings { get; } = new(StringComparer.Ordinal);

    public static SourceProjectMetadata Read(string directory)
    {
        var data = MetadataJson.Parse(File.ReadAllText(Path.Combine(directory, FileName)), "source project metadata");
        if (data["format"]?.ToString() != "eastward-source-project" || data["version"]?.ToString() != "1")
        {
            throw new InvalidDataException("Unsupported source project metadata format");
        }

        var metadata = new SourceProjectMetadata();
        foreach (var (name, value) in MetadataJson.Object(data["assets"], "assets"))
        {
            var path = MetadataJson.RequiredText(value, "assets." + name);
            SafePath.ResolveWithin(directory, path);
            metadata.Assets.Add(name, path);
        }
        foreach (var (original, value) in MetadataJson.Object(data["pathRemappings"], "pathRemappings"))
        {
            metadata.PathRemappings.Add(original, MetadataJson.RequiredText(value, "pathRemappings." + original));
        }
        return metadata;
    }

    public void SaveTo(string directory)
    {
        MetadataJson.Save(Path.Combine(directory, FileName), JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }
}
