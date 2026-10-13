using System.Text;
using EastwardLib.MetaData;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class MeshAsset : PackageAsset
{
    public string MeshData
    {
        get;
    }
    public IReadOnlyDictionary<string, byte[]> TextureFiles
    {
        get;
    }

    public MeshAsset(AssetManager manager, AssetInfo info) : this(ReadSource(manager, info)) { }

    private MeshAsset((string Xml, Dictionary<string, byte[]> Textures) source) : base(CreateChildren(source.Xml, source.Textures))
    {
        MeshData = source.Xml;
        TextureFiles = source.Textures;
    }

    public override Asset CompileSource(string path)
    {
        var source = Directory.Exists(path) ? Path.Combine(path, "output.obj") : path;
        var xml = Compilation.MeshCompiler.Compile(File.ReadAllText(source), MeshData);
        var textures = TextureFiles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.png"))
            {
                textures[Path.GetFileName(file)] = File.ReadAllBytes(file);
            }
        }
        return new MeshAsset((xml, textures));
    }

    private static (string Xml, Dictionary<string, byte[]> Textures) ReadSource(AssetManager manager, AssetInfo info)
    {
        var path = info.ObjectFiles["mesh"];
        var xml = Encoding.UTF8.GetString(manager.RequireFile(path + "/mesh"));
        var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in MeshConverter.Convert(xml).Textures)
        {
            if (manager.LoadFile(path + "/" + name) is byte[] bytes)
            {
                textures[name] = bytes;
            }
        }
        return (xml, textures);
    }

    private static Dictionary<string, Asset> CreateChildren(string xml, Dictionary<string, byte[]> textures)
    {
        var mesh = MeshConverter.Convert(xml);
        var children = new Dictionary<string, Asset>(StringComparer.Ordinal)
        {
            ["output.obj"] = new TextAsset(mesh.Obj),
            ["output.mtl"] = new TextAsset(mesh.Mtl)
        };
        foreach (var (name, bytes) in textures)
        {
            children[name] = new BinaryAsset(bytes);
        }
        return children;
    }
}
