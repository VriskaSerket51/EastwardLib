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

    public override Asset CompileSource(string path)
    {
        var layers = Compilation.PsdReader.Read(path);
        var images = new Dictionary<string, RgbaImage>(StringComparer.Ordinal);
        foreach (var name in Textures.Keys)
        {
            var layer = layers.SingleOrDefault(layer => layer.Name == name);
            images.Add(name, layer?.Image ?? throw new InvalidDataException("Missing texture-pack layer: " + name));
        }
        return new TexturePackAsset(images);
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
