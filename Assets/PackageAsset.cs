namespace EastwardLib.Assets;

public class PackageAsset : Asset
{
    protected readonly Dictionary<string, Asset> Children;

    public PackageAsset(Dictionary<string, Asset> assets)
    {
        Children = assets;
    }

    public void AddChild(string assetName, Asset asset)
    {
        Children.Add(assetName, asset);
    }

    public void AddChildren(Dictionary<string, Asset> assets)
    {
        foreach (var (key, value) in assets)
        {
            Children.Add(key, value);
        }
    }

    public override byte[] Encode()
    {
        throw new Exception("You cannot encode/decode PackageAsset!!!");
    }

    public override void Decode(Stream s)
    {
        throw new Exception("You cannot encode/decode PackageAsset!!!");
    }

    public override void SaveTo(string path)
    {
        PrepareDirectory(path);
        foreach (var (key, value) in Children)
        {
            value.SaveTo(Path.Combine(path, key));
        }
    }
}