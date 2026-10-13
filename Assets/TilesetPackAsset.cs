using System.Globalization;
using System.Text.Json.Nodes;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class TilesetPackAsset : PackageAsset
{
    public TilesetPackAsset(JsonNode definition, RgbaImage atlas) : base(new(StringComparer.Ordinal)
    {
        ["tileset.json"] = new JsonAsset(definition),
        ["atlas.png"] = new HmgAsset(atlas)
    })
    {
    }

    public byte[] ToPsd()
    {
        var data = ((JsonAsset)Items["tileset.json"]).Data!;
        var atlas = ((HmgAsset)Items["atlas.png"]).Image!;
        static int Number(JsonNode? value) => int.Parse(value!.ToString(), CultureInfo.InvariantCulture);
        var layers = data["themes"]!.AsArray().Select(theme => new PsdWriter.Layer(theme!["raw_name"]!.ToString(), Children:
            theme["groups"]!.AsArray().Select(group => new PsdWriter.Layer(group!["raw_name"]!.ToString(), Children:
                group["tiles"]!.AsArray().OrderByDescending(tile => Number(tile!["raw_index"])).Select(tile =>
                {
                    var rect = tile!["rect"]!.AsArray().Select(Number).ToArray();
                    var raw = tile["raw_rect"]!.AsArray().Select(Number).ToArray();
                    if (raw[2] - raw[0] != rect[2] || raw[3] - raw[1] != rect[3])
                    {
                        throw new InvalidDataException("Tileset source and atlas dimensions differ");
                    }

                    return new PsdWriter.Layer(tile["basename"]!.ToString(), atlas.Crop(rect[0], rect[1], rect[2], rect[3]), raw[0], raw[1]);
                }).ToArray())).ToArray())).ToArray();
        return PsdWriter.Write(layers);
    }

    public override void SaveTo(string path)
    {
        if (Path.GetExtension(path).Equals(".psd", StringComparison.OrdinalIgnoreCase))
        {
            path = SafePath.PrepareFile(path);
            File.WriteAllBytes(path, ToPsd());
        }
        else
        {
            base.SaveTo(path);
        }
    }

    public override Asset CompileSource(string path)
    {
        var layers = Compilation.PsdReader.Read(path);
        var data = ((JsonAsset)Items["tileset.json"]).Data!;
        var original = ((HmgAsset)Items["atlas.png"]).Image!;
        var atlas = new RgbaImage(original.Width, original.Height, (byte[])original.Pixels.Clone());
        foreach (var theme in data["themes"]!.AsArray())
        {
            foreach (var group in theme!["groups"]!.AsArray())
            {
                foreach (var tile in group!["tiles"]!.AsArray())
                {
                    var themeLayer = layers.Single(layer => layer.Name == theme["raw_name"]!.ToString());
                    var groupLayer = themeLayer.Children!.Single(layer => layer.Name == group["raw_name"]!.ToString());
                    var layer = groupLayer.Children!.Single(layer => layer.Name == tile!["basename"]!.ToString());
                    var rect = tile!["rect"]!.AsArray().Select(value => int.Parse(value!.ToString(), CultureInfo.InvariantCulture)).ToArray();
                    var raw = tile["raw_rect"]!.AsArray().Select(value => int.Parse(value!.ToString(), CultureInfo.InvariantCulture)).ToArray();
                    if (layer.Image == null || layer.Image.Width != rect[2] || layer.Image.Height != rect[3] || layer.X != raw[0] || layer.Y != raw[1])
                    {
                        throw new InvalidDataException("Keep indexed tileset layer bounds: " + layer.Name);
                    }

                    atlas.Crop(rect[0], rect[1], rect[2], rect[3]);
                    for (var row = 0; row < rect[3]; row++)
                    {
                        layer.Image.Pixels.AsSpan(row * rect[2] * 4, rect[2] * 4).CopyTo(atlas.Pixels.AsSpan(((rect[1] + row) * atlas.Width + rect[0]) * 4));
                    }
                }
            }
        }

        return new TilesetPackAsset(data.DeepClone(), atlas);
    }
}
