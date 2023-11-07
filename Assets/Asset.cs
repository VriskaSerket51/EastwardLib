using EastwardLib.MetaData;

namespace EastwardLib.Assets;

public class Asset
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

    public virtual byte[] Encode()
    {
        throw new Exception();
    }

    public virtual void Decode(Stream s)
    {
        throw new Exception();
    }

    protected void PrepareDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory == null)
        {
            throw new Exception();
        }

        Directory.CreateDirectory(directory);
    }

    public virtual void SaveTo(string path)
    {
    }
}