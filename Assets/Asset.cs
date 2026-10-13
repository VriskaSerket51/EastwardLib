namespace EastwardLib.Assets;

public abstract class Asset
{
    public static T Create<T>(byte[] data) where T : Asset, new()
    {
        using var stream = new MemoryStream(data);
        return Create<T>(stream);
    }

    public static T Create<T>(Stream stream) where T : Asset, new()
    {
        var asset = new T();
        asset.Decode(stream);
        return asset;
    }

    public abstract byte[] Encode();
    public abstract void Decode(Stream stream);
    public abstract void SaveTo(string path);

    /// <summary>Read edited source into a new asset without changing this instance.</summary>
    public virtual Asset CompileSource(string path)
    {
        using var stream = File.OpenRead(path);
        return CompileSource(stream);
    }

    /// <summary>Compile single-file source without taking ownership of the caller's stream.</summary>
    public virtual Asset CompileSource(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        throw new NotSupportedException("Stream source compilation is not supported for " + GetType().Name);
    }
}
