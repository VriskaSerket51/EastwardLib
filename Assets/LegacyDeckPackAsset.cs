using System.Globalization;
using System.Text.Json.Nodes;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public sealed class LegacyDeckPackAsset : TextAsset
{
    private readonly AssetManager _manager;
    private readonly Dictionary<string, JsonNode> _decks;
    public IReadOnlyList<string> DeckNames => _decks.Keys.ToArray();
    public LegacyDeckPackAsset(AssetManager manager, string source) : base(source)
    {
        _manager = manager;
        var data = JsonNode.Parse(source)!;
        var map = data["map"]!;
        var entries = map[data["root"]!.ToString()]!["body"]!["decks"]!.AsArray();
        var decks = entries.Select(value => value is JsonObject ? value["body"] ?? value : map[value!.ToString()]!["body"]!);
        _decks = decks.ToDictionary(value => value!["name"]!.ToString(), value => value!, StringComparer.Ordinal);
    }
    public LegacyDeckAsset GetDeck(string name)
    {
        var definition = _decks[name];
        var path = definition["texture"]?.ToString();
        var texture = path == null ? null : (_manager.LoadAsset(path) as HmgAsset)?.Image;
        return new LegacyDeckAsset(definition, texture);
    }
    public override Asset CompileSource(Stream stream)
    {
        return new LegacyDeckPackAsset(_manager, Create<TextAsset>(stream).Text);
    }
}

public sealed class LegacyDeckAsset : JsonAsset
{
    public RgbaImage? Texture
    {
        get;
    }
    private int Integer(string key, int fallback = 0)
    {
        return Data?[key] == null ? fallback : checked((int)double.Parse(Data[key]!.ToString(), CultureInfo.InvariantCulture));
    }
    public IReadOnlyList<string> Entries
    {
        get
        {
            if (Texture == null)
            {
                return [];
            }

            if (Data?["type"]?.ToString() is "tileset" or "quad_array")
            {
                var strideX = Math.Max(1, Integer("tw", 32) + Integer("spacing"));
                var strideY = Math.Max(1, Integer("th", 32) + Integer("spacing"));
                var count = Texture.Width / strideX * (Texture.Height / strideY);
                var limit = Integer("count", -1);
                if (limit > 0)
                {
                    count = Math.Min(count, limit);
                }

                return new[] { "Atlas" }.Concat(Enumerable.Range(1, count).Select(index => index.ToString())).ToArray();
            }
            return ["Preview"];
        }
    }
    public LegacyDeckAsset(JsonNode definition, RgbaImage? texture) : base(definition) { Texture = texture; }
    public RgbaImage? Render(string? entry)
    {
        if (Texture == null)
        {
            return null;
        }

        if (entry == "Atlas")
        {
            return Texture;
        }

        if (Data?["type"]?.ToString() is "tileset" or "quad_array")
        {
            var tw = Math.Max(1, Integer("tw", 32));
            var th = Math.Max(1, Integer("th", 32));
            var spacing = Integer("spacing");
            var columns = Texture.Width / Math.Max(1, tw + spacing);
            var rows = Texture.Height / Math.Max(1, th + spacing);
            if (columns == 0 || rows == 0)
            {
                return null;
            }

            var index = entry == null ? 0 : int.Parse(entry, CultureInfo.InvariantCulture) - 1;
            var row = index / columns;
            var column = index % columns;
            var x = column * (tw + spacing) + Integer(Data?["type"]?.ToString() == "tileset" ? "ox" : "offx");
            var y = row * (th + spacing) + Integer(Data?["type"]?.ToString() == "tileset" ? "oy" : "offy");
            return Texture.Crop(x, y, tw, th);
        }
        var width = Math.Max(1, Integer("w", Texture.Width));
        var height = Math.Max(1, Integer("h", Texture.Height));
        if ((long)width * height > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Deck preview exceeds image size limit");
        }

        var image = new RgbaImage(width, height, new byte[checked(width * height * 4)]);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Texture.Pixels.AsSpan(((y * Texture.Height / height) * Texture.Width + x * Texture.Width / width) * 4, 4).CopyTo(image.Pixels.AsSpan((y * width + x) * 4));
            }
        }

        return image;
    }
}
