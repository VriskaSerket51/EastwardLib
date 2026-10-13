using System.Text;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class LuaAsset : BinaryAsset
{
    private readonly Func<byte[], string>? _decompiler;
    private string? _source;
    public bool IsBytecode => Encode().AsSpan().StartsWith(new byte[] { 0x1b, 0x4c, 0x4a });

    public LuaAsset()
    {
    }

    public LuaAsset(byte[] bytes, Func<byte[], string>? decompiler = null) : base(bytes)
    {
        _decompiler = decompiler;
    }

    public override void Decode(Stream stream)
    {
        base.Decode(stream);
        _source = null;
    }

    /// <summary>Decompile only when editor source is explicitly requested.</summary>
    public string GetSource()
    {
        if (_source != null)
        {
            return _source;
        }

        var bytes = Encode();
        _source = IsBytecode
            ? (_decompiler ?? throw new NotSupportedException("Configure AssetManager.LuaDecompiler to extract LuaJIT source"))(bytes)
            : Encoding.UTF8.GetString(bytes);
        return _source;
    }

    public void SaveSourceTo(string path)
    {
        path = SafePath.PrepareFile(path);
        File.WriteAllText(path, GetSource());
    }
}
