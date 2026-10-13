using System.Text.Json.Nodes;
using EastwardLib.MetaData;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class MSpriteAsset : JsonAsset
{
    private readonly AssetManager _manager;
    private readonly AssetInfo _info;
    private IReadOnlyDictionary<string, RgbaImage>? _compiledAtlases;
    public IReadOnlyDictionary<string, RgbaImage> AtlasImages => _compiledAtlases ?? LoadAtlases();

    public MSpriteAsset(AssetManager manager, AssetInfo info) : base(manager.LoadDefinition(info).Data)
    {
        _manager = manager;
        _info = info;
    }

    public byte[] ToAse()
    {
        return AseWriter.Write(Data!, AtlasImages);
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

    public override Asset CompileSource(string path)
    {
        var atlas = Data!["atlases"]!.AsObject().First();
        var image = AtlasImages[atlas.Key];
        var result = Compilation.SpriteCompiler.Compile(path, Data, atlas.Key, atlas.Value!.ToString(), image.Width, image.Height, image);
        var names = Data["anims"]!.AsObject().Select(pair => pair.Value!).Where(animation => animation["seq"]!.AsArray().Count > 0).Select(animation => animation["name"]!.ToString()).Order(StringComparer.Ordinal);
        var editedNames = result.Definition["anims"]!.AsObject().Select(pair => pair.Value!["name"]!.ToString()).Order(StringComparer.Ordinal);
        if (!names.SequenceEqual(editedNames))
        {
            throw new NotSupportedException("Keep indexed sprite animation names; new virtual sequences require index registration");
        }

        return new MSpriteAsset(_manager, _info)
        {
            Data = result.Definition,
            _compiledAtlases = new Dictionary<string, RgbaImage>(StringComparer.Ordinal) { [atlas.Key] = result.Atlas }
        };
    }

    public void SaveSourceTo(string path)
    {
        var atlases = AtlasImages;
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

    public RgbaImage RenderFrame(string name, int index)
    {
        static int Number(JsonNode? value) => checked((int)double.Parse(value!.ToString(), System.Globalization.CultureInfo.InvariantCulture));
        var animation = Data!["anims"]!.AsObject().Select(pair => pair.Value!).FirstOrDefault(value => value["name"]?.ToString() == name)
            ?? throw new InvalidDataException("Missing sprite animation: " + name);
        var sequence = animation["seq"]!.AsArray();
        if (index < 0 || index >= sequence.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var entry = sequence[index]!.AsArray();
        var frame = Data["frames"]![entry[0]!.ToString()]!;
        var atlases = AtlasImages;
        var parts = frame["parts"]!.AsArray().Select(part =>
        {
            var module = Data["modules"]![part![0]!.ToString()]!;
            var rect = module["rect"]!.AsArray().Select(Number).ToArray();
            var atlas = module["atlas"]?.ToString();
            var image = atlas != null ? atlases[atlas] : atlases.Count == 1 ? atlases.Values.First() : throw new InvalidDataException("Missing sprite atlas");
            return (Image: image.Crop(rect[0], rect[1], rect[2], rect[3]), X: Number(part[1]) + (entry.Count > 2 ? Number(entry[2]) : 0), Y: Number(part[2]) + (entry.Count > 3 ? Number(entry[3]) : 0));
        }).ToArray();
        var left = parts.Select(part => part.X).Append(0).Min();
        var top = parts.Select(part => part.Y).Append(0).Min();
        var right = parts.Select(part => checked(part.X + part.Image.Width)).Append(1).Max();
        var bottom = parts.Select(part => checked(part.Y + part.Image.Height)).Append(1).Max();
        var result = new RgbaImage(checked(right - left), checked(bottom - top), new byte[checked((right - left) * (bottom - top) * 4)]);
        foreach (var part in parts)
        {
            for (var y = 0; y < part.Image.Height; y++)
            {
                for (var x = 0; x < part.Image.Width; x++)
                {
                    var source = (y * part.Image.Width + x) * 4;
                    var target = ((part.Y - top + y) * result.Width + part.X - left + x) * 4;
                    var alpha = part.Image.Pixels[source + 3] / 255.0;
                    var previous = result.Pixels[target + 3] / 255.0;
                    var combined = alpha + previous * (1 - alpha);
                    if (combined == 0)
                    {
                        continue;
                    }

                    for (var channel = 0; channel < 3; channel++)
                    {
                        result.Pixels[target + channel] = (byte)Math.Clamp(Math.Round((part.Image.Pixels[source + channel] * alpha + result.Pixels[target + channel] * previous * (1 - alpha)) / combined), 0, 255);
                    }

                    result.Pixels[target + 3] = (byte)Math.Clamp(Math.Round(combined * 255), 0, 255);
                }
            }
        }
        return result;
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
