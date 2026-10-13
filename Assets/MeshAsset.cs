using System.Text;
using EastwardLib.MetaData;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class MeshAsset : PackageAsset
{
    public MeshAsset(AssetManager manager, AssetInfo info)
        : base(CreateChildren(manager, info))
    {
    }

    private static Dictionary<string, Asset> CreateChildren(AssetManager manager, AssetInfo info)
    {
        var path = info.ObjectFiles["mesh"];
        var mesh = MeshConverter.Convert(Encoding.UTF8.GetString(manager.RequireFile(path + "/mesh")));
        var children = new Dictionary<string, Asset>(StringComparer.Ordinal)
        {
            ["output.obj"] = new TextAsset(mesh.Obj),
            ["output.mtl"] = new TextAsset(mesh.Mtl)
        };
        foreach (var name in mesh.Textures)
        {
            if (manager.LoadFile(path + "/" + name) is byte[] bytes)
            {
                children[name] = new BinaryAsset(bytes);
            }
        }

        return children;
    }
}
