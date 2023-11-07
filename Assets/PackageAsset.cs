namespace EastwardLib.Assets;

public class PackageAsset : Asset
{
    private Dictionary<string, Asset> _childs;

    public PackageAsset(Dictionary<string, Asset> assets)
    {
        _childs = assets;
    }

    public void AddChild(string assetName, Asset asset)
    {
        _childs.Add(assetName, asset);
    }

    public void AddChildren(Dictionary<string, Asset> assets)
    {
        foreach (var (key, value) in assets)
        {
            _childs.Add(key, value);
        }
    }

    public override void SaveTo(string path)
    {
        PrepareDirectory(path);
        foreach (var (key, value) in _childs)
        {
            value.SaveTo(Path.Combine(path, key));
        }
    }
}