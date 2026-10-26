using System.Text.Json.Nodes;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class AtlasAsset : PackageAsset
{
    private readonly Dictionary<string, byte[]> _images = new(StringComparer.Ordinal);

    public AtlasAsset(Dictionary<string, Asset> assets) : base(assets)
    {
        var definition = JsonNode.Parse(((TextAsset)assets["atlas.json"]).Text) ?? throw new InvalidDataException("Missing atlas index");
        var atlases = definition["atlases"]!.AsArray().Select(item =>
        {
            var asset = assets[item!["name"]!.GetValue<string>()];
            if (asset is HmgAsset hmg)
            {
                return hmg.Image!;
            }

            using var stream = new MemoryStream(asset.Encode());
            return RgbaImage.Load(stream);
        }).ToArray();
        foreach (var item in definition["items"]!.AsArray())
        {
            var name = item!["name"]!.GetValue<string>();
            var rect = item["rect"]!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
            var atlas = item["atlas"]!.GetValue<int>();
            if (rect.Length != 4 || atlas < 0 || atlas >= atlases.Length)
            {
                throw new InvalidDataException("Invalid atlas item");
            }

            _images.Add(name, atlases[atlas].Crop(rect[0], rect[1], rect[2], rect[3]).ToPng());
        }
    }

    internal IReadOnlyDictionary<string, byte[]> Images => _images;

    public byte[] this[string name] => _images[name];
    public override void SaveTo(string path)
    {
        foreach (var (name, data) in _images)
        {
            var target = SafePath.ResolveWithin(path, name);
            target = SafePath.PrepareFile(target);
            File.WriteAllBytes(target, data);
        }
    }
}
