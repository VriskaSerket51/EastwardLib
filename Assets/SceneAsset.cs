using EastwardLib.Utils;
using System.Text.Json.Nodes;

namespace EastwardLib.Assets;

public class SceneAsset : PackageAsset
{
    public JsonNode? Index
    {
        get;
    }

    public IReadOnlyDictionary<string, JsonNode?> Groups
    {
        get;
    }

    private readonly JsonAsset? _single;

    public SceneAsset(AssetManager manager, MetaData.AssetInfo info) : base(new(StringComparer.Ordinal))
    {
        var source = info.ObjectFiles.GetValueOrDefault("def");
        var compiled = info.ObjectFiles.GetValueOrDefault("compiled");
        var groups = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (source != null && manager.FileExists(source + "/scene_index.json"))
        {
            Index = manager.LoadJson(source + "/scene_index.json");
            foreach (var name in manager.GetFiles(source!).Where(name => !name.Contains('/') && name.EndsWith(".scene_group", StringComparison.Ordinal)))
            {
                var data = manager.LoadJson(source + "/" + name) ?? manager.LoadPacked(compiled + "/" + name + ".packed");
                if (data == null)
                {
                    throw new InvalidDataException("Missing scene group: " + name);
                }

                groups.Add(name, data);
            }
            foreach (var name in manager.GetFiles(source!))
            {
                if (name != "scene_index.json" && !(name.EndsWith(".scene_group", StringComparison.Ordinal) && !name.Contains('/')))
                {
                    Children.Add(name, new BinaryAsset(manager.LoadFile(source + "/" + name)!));
                }
            }
        }
        else if (manager.FileExists(source))
        {
            Index = manager.LoadJson(source);
            _single = new JsonAsset(Index);
        }
        else if (compiled != null && manager.FileExists(compiled + "/scene_index.json.packed"))
        {
            Index = manager.LoadPacked(compiled + "/scene_index.json.packed");
            foreach (var name in manager.GetFiles(compiled!).Where(name => !name.Contains('/') && name.EndsWith(".scene_group.packed", StringComparison.Ordinal)))
            {
                groups.Add(name[..^7], manager.LoadPacked(compiled + "/" + name));
            }
        }
        else
        {
            throw new FileNotFoundException("Missing scene definition", info.AssetName);
        }

        Groups = groups;
        if (_single == null)
        {
            Children.Add("scene_index.json", new JsonAsset(Index));
            foreach (var (name, data) in groups)
            {
                Children.Add(name, new JsonAsset(data));
            }
        }
    }

    public override void SaveTo(string path)
    {
        if (_single != null)
        {
            _single.SaveTo(path);
        }
        else
        {
            base.SaveTo(path);
        }
    }
}
