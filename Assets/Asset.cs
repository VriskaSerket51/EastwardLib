using EastwardLib.MetaData;

namespace EastwardLib.Assets;

public abstract class Asset
{
    public static T Create<T>(byte[] data) where T : Asset, new()
    {
        using MemoryStream ms = new MemoryStream(data);

        T asset = new T();
        asset.Decode(ms);

        return asset;
    }

    public static T Create<T>(Stream s) where T : Asset, new()
    {
        T asset = new T();
        asset.Decode(s);

        return asset;
    }

    public abstract byte[] Encode();

    public abstract void Decode(Stream s);

    protected void PrepareDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory == null)
        {
            throw new Exception();
        }

        Directory.CreateDirectory(directory);
    }

    public abstract void SaveTo(string path);
}