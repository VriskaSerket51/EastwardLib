using EastwardLib.Assets;

namespace EastwardLib.Utils;

internal sealed class AssetExporter(string root, bool luaSource)
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    public void SaveTo(Asset asset, string name)
    {
        if (asset is AtlasAsset atlas)
        {
            PrepareDirectory(name);
            foreach (var (child, bytes) in atlas.Images)
            {
                Write(Path.Combine(name, Path.ChangeExtension(child, ".png")), path => File.WriteAllBytes(SafePath.PrepareFile(path), bytes));
            }
            return;
        }
        if (asset is SceneAsset scene && scene.Items.Count == 0)
        {
            SaveTo(new JsonAsset(scene.Index), name);
            return;
        }
        var psd = asset is TexturePackAsset or TilesetPackAsset && Path.GetExtension(name).Equals(".psd", StringComparison.OrdinalIgnoreCase);
        if (asset is PackageAsset package && !psd)
        {
            PrepareDirectory(name);
            foreach (var (child, item) in package.Items)
            {
                SaveTo(item, Path.Combine(name, child));
            }
            return;
        }
        var extension = asset switch
        {
            HmgAsset => ".png",
            WorkbookAsset => ".xlsx",
            MSpriteAsset => ".ase",
            SQScriptAsset => ".sq",
            CsvAsset => ".csv",
            JsonAsset or PackedAsset or MultiTextureAsset or LegacyDeckPackAsset => ".json",
            GraphAsset graph => graph.GraphType == "fsm_scheme" ? ".lua" : ".json",
            LuaAsset lua => luaSource || !lua.IsBytecode ? ".lua" : ".luac",
            _ => null
        };
        var output = extension == null ? name : Path.ChangeExtension(name, extension);
        Write(output, path =>
        {
            if (luaSource && asset is LuaAsset lua)
            {
                lua.SaveSourceTo(path);
            }
            else
            {
                asset.SaveTo(path);
            }
        });
    }

    private void Write(string name, Action<string> save)
    {
        var path = SafePath.ResolveWithin(root, name);
        if (_files.Contains(path) || _directories.Contains(path))
        {
            throw new IOException("Decoded asset output paths collide: " + name);
        }
        PrepareParents(path);
        _files.Add(path);
        save(path);
    }

    private void PrepareDirectory(string name)
    {
        var path = SafePath.ResolveWithin(root, name);
        if (_files.Contains(path))
        {
            throw new IOException("Decoded asset output paths collide: " + name);
        }
        PrepareParents(path);
        _directories.Add(path);
        Directory.CreateDirectory(path);
    }

    private void PrepareParents(string path)
    {
        for (var parent = Path.GetDirectoryName(path); parent != null; parent = Path.GetDirectoryName(parent))
        {
            if (_files.Contains(parent))
            {
                throw new IOException("Decoded asset output paths collide: " + parent);
            }
            _directories.Add(parent);
        }
    }
}
