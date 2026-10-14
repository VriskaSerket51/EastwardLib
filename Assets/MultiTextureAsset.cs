using System.Text.Json.Nodes;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public sealed class MultiTextureAsset : TextAsset
{
    private readonly AssetManager _manager;
    private readonly JsonObject _textures;
    public IReadOnlyList<string> Entries => _textures.Select(pair => pair.Key).ToArray();
    public MultiTextureAsset(AssetManager manager, string source) : base(source)
    {
        _manager = manager;
        _textures = JsonNode.Parse(source)!["textures"]!.AsObject();
    }
    public RgbaImage? GetImage(string name)
    {
        return (_manager.LoadAsset(_textures[name]!.ToString()) as HmgAsset)?.Image;
    }
    public override Asset CompileSource(Stream stream)
    {
        return new MultiTextureAsset(_manager, Create<TextAsset>(stream).Text);
    }
}
