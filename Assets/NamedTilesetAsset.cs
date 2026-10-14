using System.Globalization;
using System.Text.Json.Nodes;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public sealed class NamedTilesetAsset : JsonAsset
{
    public RgbaImage Atlas
    {
        get;
    }
    private readonly Dictionary<string, JsonNode> _tiles;
    public IReadOnlyList<string> Entries => _tiles.Keys.ToArray();
    public NamedTilesetAsset(JsonNode definition, RgbaImage atlas) : base(definition)
    {
        Atlas = atlas;
        _tiles = Data!["groups"]!.AsArray().SelectMany(group => group!["tiles"]!.AsArray()).ToDictionary(tile => tile!["name"]!.ToString(), tile => tile!, StringComparer.Ordinal);
    }
    public RgbaImage? Render(string? entry)
    {
        entry ??= Entries.FirstOrDefault();
        if (entry == null)
        {
            return null;
        }

        var rect = _tiles[entry]["rect"]!.AsArray().Select(value => int.Parse(value!.ToString(), CultureInfo.InvariantCulture)).ToArray();
        return Atlas.Crop(rect[0], rect[1], rect[2], rect[3]);
    }
}
