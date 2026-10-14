using System.Text.Json.Nodes;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public sealed class DeckPackAsset : PackageAsset
{
    private readonly Dictionary<string, JsonNode> _definitions;
    private readonly Dictionary<string, DeckAsset> _decks = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, RgbaImage> _textures;
    public IReadOnlyList<string> DeckNames
    {
        get;
    }

    public DeckPackAsset(Dictionary<string, Asset> assets) : base(assets)
    {
        var definition = (JsonAsset)assets["decks.json"];
        _definitions = definition.Data!["decks"]!.AsArray().ToDictionary(deck => deck!["name"]!.ToString(), deck => deck!, StringComparer.Ordinal);
        DeckNames = _definitions.Keys.ToArray();
        _textures = assets.Where(pair => pair.Value is HmgAsset).ToDictionary(pair => pair.Key == "decks_n.png" ? "Normal" : "Color", pair => ((HmgAsset)pair.Value).Image!, StringComparer.Ordinal);
    }

    public string GetDeckType(string name)
    {
        return _definitions[name]["type"]?.ToString() ?? "deck";
    }

    public DeckAsset GetDeck(string name)
    {
        if (!_decks.TryGetValue(name, out var deck))
        {
            deck = new DeckAsset(_definitions[name], _textures);
            _decks.Add(name, deck);
        }
        return deck;
    }

    public override Asset CompileSource(string path)
    {
        var package = (PackageAsset)base.CompileSource(path);
        return new DeckPackAsset(package.Items.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }
}
