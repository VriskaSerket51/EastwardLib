using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class BinaryAsset : Asset
{
    private byte[]? _data;

    public BinaryAsset()
    {
    }

    public BinaryAsset(byte[] data)
    {
        _data = data;
    }

    public override byte[] Encode()
    {
        return _data ?? throw new InvalidOperationException("No binary data loaded");
    }

    public override Asset CompileSource(Stream stream)
    {
        return Create<BinaryAsset>(stream);
    }

    public override void Decode(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        _data = buffer.ToArray();
    }

    public override void SaveTo(string path)
    {
        path = SafePath.PrepareFile(path);
        File.WriteAllBytes(path, Encode());
    }
}
