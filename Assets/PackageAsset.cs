using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class PackageAsset : Asset
{
    protected readonly Dictionary<string, Asset> Children;
    public IReadOnlyDictionary<string, Asset> Items => Children;

    public PackageAsset(Dictionary<string, Asset> assets)
    {
        Children = assets;
    }

    public void AddChild(string name, Asset asset)
    {
        Children.Add(name, asset);
    }

    public void AddChildren(Dictionary<string, Asset> assets)
    {
        foreach (var (name, asset) in assets)
        {
            AddChild(name, asset);
        }
    }

    public override byte[] Encode()
    {
        throw new NotSupportedException("A package contains multiple resource files; encode its items individually");
    }

    public override Asset CompileSource(string path)
    {
        var items = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var (name, asset) in Children)
        {
            items.Add(name, asset.CompileSource(SafePath.ResolveWithin(path, name)));
        }
        return new PackageAsset(items);
    }

    public override void Decode(Stream stream)
    {
        throw new NotSupportedException("Load package items through AssetManager");
    }

    public override void SaveTo(string path)
    {
        foreach (var (name, asset) in Children)
        {
            asset.SaveTo(SafePath.ResolveWithin(path, name));
        }
    }
}
