using System.Text.Json.Nodes;
using EastwardLib.MetaData;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class MSpriteAsset : JsonAsset
{
    private readonly AssetManager _manager;
    private readonly AssetInfo _info;

    public MSpriteAsset(AssetManager manager, AssetInfo info) : base(manager.LoadDefinition(info).Data)
    {
        _manager = manager;
        _info = info;
    }

    public byte[] ToAse()
    {
        return AseWriter.Write(Data!, LoadAtlases());
    }

    private Dictionary<string, RgbaImage> LoadAtlases()
    {
        var children = _manager.GetChildren(_info.AssetName).Where(child => child.Type == "texture").ToArray();
        var refs = Data!["atlases"]!.AsObject();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var atlases = new Dictionary<string, RgbaImage>(StringComparer.Ordinal);
        foreach (var (id, value) in refs)
        {
            var path = value!.ToString();
            var basename = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
            var matches = children.Where(child => child.AssetName == path || child.Name == basename || child.ObjectFiles.Values.Contains(path)).ToArray();
            var child = matches.Length == 1 ? matches[0] : matches.Length == 0 && refs.Count == 1 && children.Length == 1 ? children[0]
                : throw new InvalidDataException("Missing or ambiguous sprite atlas: " + id);
            if (!paths.Add(child.AssetName))
            {
                throw new InvalidDataException("Sprite atlases resolve to the same texture");
            }

            atlases.Add(id, ((HmgAsset)_manager.LoadAsset(child.AssetName)!).Image!);
        }
        return atlases;
    }

    public void SaveSourceTo(string path)
    {
        var atlases = LoadAtlases();
        var animations = Data!["anims"]!.AsObject().Select(pair => pair.Value!).ToArray();
        var bases = animations.Where(animation => !animation["name"]!.ToString().Contains(':')).ToArray();
        if (animations.Any(animation => animation["name"]!.ToString().Contains(':') && !bases.Any(parent => animation["name"]!.ToString().StartsWith(parent["name"] + ":", StringComparison.Ordinal))))
        {
            throw new InvalidDataException("Missing source animation for sprite tag");
        }

        var count = 0;
        foreach (var animation in bases)
        {
            var sequence = animation["seq"]!.AsArray();
            if (sequence.Count == 0)
            {
                continue;
            }

            var name = animation["name"]!.ToString();
            var tags = new List<AseWriter.Tag>();
            foreach (var sub in animations.Where(value => value["name"]!.ToString().StartsWith(name + ":", StringComparison.Ordinal) && value["seq"]!.AsArray().Count > 0))
            {
                var entries = sub["seq"]!.AsArray();
                var first = Enumerable.Range(0, Math.Max(0, sequence.Count - entries.Count + 1)).FirstOrDefault(index => entries.Select((entry, offset) => JsonNode.DeepEquals(entry, sequence[index + offset])).All(equal => equal), -1);
                if (first < 0)
                {
                    throw new InvalidDataException("Sprite tag is not a contiguous source sequence: " + sub["name"]);
                }

                tags.Add(new AseWriter.Tag((sub["deprecated"]?.ToString() is "true" or "True" ? "///" : "") + sub["name"]!.ToString()[(name.Length + 1)..], first, first + entries.Count - 1));
            }
            var definition = Data.DeepClone();
            definition["anims"] = new JsonObject { ["source"] = animation.DeepClone() };
            var output = SafePath.ResolveWithin(path, ProjectPath.Escape(name) + ".ase");
            output = SafePath.PrepareFile(output);
            File.WriteAllBytes(output, AseWriter.Write(definition, atlases, tags));
            count++;
        }
        if (count == 0)
        {
            throw new InvalidDataException("No sprite animation frames");
        }

        new JsonAsset(Data).SaveTo(SafePath.ResolveWithin(path, "runtime-definition.json"));
        foreach (var (id, image) in atlases)
        {
            image.SavePng(SafePath.ResolveWithin(path, atlases.Count == 1 ? "atlas.png" : "atlases/" + ProjectPath.Escape(id) + ".png"));
        }
    }

    public override void SaveTo(string path)
    {
        if (Path.GetExtension(path).Equals(".ase", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".aseprite", StringComparison.OrdinalIgnoreCase))
        {
            path = SafePath.PrepareFile(path);
            File.WriteAllBytes(path, ToAse());
        }
        else
        {
            base.SaveTo(path);
        }
    }

    public JsonNode FindAnimation(string name)
    {
        var animation = Data!["anims"]!.AsObject().Select(pair => pair.Value)
            .FirstOrDefault(value => value?["name"]?.ToString() == name)
            ?? throw new InvalidDataException("Missing sprite animation: " + name);
        var frames = new JsonObject();
        foreach (var item in animation["seq"]!.AsArray())
        {
            var id = item![0]!.ToString();
            frames[id] = Data["frames"]![id]!.DeepClone();
        }
        return new JsonObject
        {
            ["animation"] = animation.DeepClone(),
            ["frames"] = frames,
            ["modules"] = Data["modules"]?.DeepClone(),
            ["features"] = Data["features"]?.DeepClone()
        };
    }
}
