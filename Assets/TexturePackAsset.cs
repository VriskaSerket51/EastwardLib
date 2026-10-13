using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class TexturePackAsset : PackageAsset
{
    public IReadOnlyDictionary<string, RgbaImage> Textures
    {
        get;
    }

    public TexturePackAsset(Dictionary<string, RgbaImage> textures)
        : base(textures.ToDictionary(pair => pair.Key + ".png", pair => (Asset)new HmgAsset(pair.Value), StringComparer.Ordinal))
    {
        Textures = textures;
    }

    public byte[] ToPsd()
    {
        return PsdWriter.Write(Textures.Select(pair => new PsdWriter.Layer(pair.Key, pair.Value)).ToArray());
    }

    public override void SaveTo(string path)
    {
        if (Path.GetExtension(path).Equals(".psd", StringComparison.OrdinalIgnoreCase))
        {
            path = SafePath.PrepareFile(path);
            File.WriteAllBytes(path, ToPsd());
        }
        else
        {
            base.SaveTo(path);
        }
    }
}
