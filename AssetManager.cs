﻿using EastwardLib.Assets;
using EastwardLib.Exceptions;
using EastwardLib.MetaData;

namespace EastwardLib;

public class AssetManager
{
    private readonly AssetIndex _assetIndex;
    private readonly ScriptLibrary _scriptLibrary;
    private TextureIndex _textureIndex;
    private readonly Dictionary<string, GArchive> _archives = new();
    private readonly Dictionary<string, Asset> _assets = new();

    public AssetManager(AssetIndex assetIndex, ScriptLibrary scriptLibrary, TextureIndex textureIndex)
    {
        _assetIndex = assetIndex;
        _scriptLibrary = scriptLibrary;
        _textureIndex = textureIndex;
    }

    public static AssetManager Create(string assetIndexPath, string scriptLibraryPath, string textureIndexPath)
    {
        if (!File.Exists(assetIndexPath))
        {
            throw new FileNotFoundException(assetIndexPath);
        }

        var assetIndex = AssetIndex.Create(File.ReadAllText(assetIndexPath));

        if (!File.Exists(scriptLibraryPath))
        {
            throw new FileNotFoundException(scriptLibraryPath);
        }

        var scriptLibrary = ScriptLibrary.Create(File.ReadAllText(scriptLibraryPath));

        if (!File.Exists(textureIndexPath))
        {
            throw new FileNotFoundException(textureIndexPath);
        }

        var textureIndex = TextureIndex.Create(File.ReadAllText(textureIndexPath));

        return new AssetManager(assetIndex, scriptLibrary, textureIndex);
    }

    public void Log()
    {
        List<string> types = new List<string>();
        foreach (var (_, value) in _assetIndex)
        {
            var fileType = value.FileType;
            var type = value.Type;
            if (types.Contains(type))
            {
                continue;
            }

            types.Add(type);
            // if (fileType == "d")
            // {
            //     Console.WriteLine($"Dir: {type}");
            // }

            if (fileType == "v")
            {
                Console.WriteLine($"Virtual: {type}");
            }
        }
    }

    // public static AssetManager Create(string path)
    // {
    //     if (!File.Exists(path))
    //     {
    //         throw new FileNotFoundException(path);
    //     }
    //
    //     var config = GArchive.Read(path);
    //     config["config/asset_index"];
    // }

    public void LoadArchive(GArchive archive)
    {
        _archives.Add(archive.ArchiveName, archive);
    }

    public void UnloadArchive(string archiveName)
    {
        _archives.Remove(archiveName);
    }

    public void LoadAssets()
    {
        foreach (var assetName in _assetIndex.Keys)
        {
            LoadAsset(assetName);
        }
    }

    public Asset? LoadAsset(string assetName)
    {
        if (_assets.TryGetValue(assetName, out var cache))
        {
            return cache;
        }

        if (_assetIndex.TryGetValue(assetName, out var assetInfo))
        {
            var asset = LoadAsset(assetInfo);
            if (asset == null)
            {
                return null;
            }

            return _assets[assetName] = asset;
        }

        return null;
    }

    public Asset? LoadAsset(AssetInfo assetInfo)
    {
        var assetName = assetInfo.AssetName;
        var fileType = assetInfo.FileType;
        var objectFiles = assetInfo.ObjectFiles;
        var type = assetInfo.Type;

        if (objectFiles.Count == 0)
        {
            if (type != "lua" && type != "texture")
            {
                return null;
            }
        }

        try
        {
            switch (type)
            {
                case "folder":
                case "file":
                    return null;
                case "deck2d.mquad":
                case "deck2d.mtileset":
                case "deck2d.quad":
                case "deck2d.quads":
                case "deck2d.stretchpatch":
                case "deck2d.tileset":
                case "deck2d.quad_array":
                    return null;
                case "texture_pack":
                case "texture_processor":
                    return null;
                case "named_tileset_pack":
                    return new PackageAsset(new Dictionary<string, Asset>(2)
                    {
                        { "atlas", Asset.Create<BinaryAsset>(LoadFile(objectFiles["atlas"])) },
                        { "def", Asset.Create<TextAsset>(LoadFile(objectFiles["def"])) }
                    });
                case "named_tileset":
                    return null;
                case "deck_pack_raw":
                case "deck_pack":
                    var deckPackage = LoadDirectory(objectFiles["export"]);
                    var deckFiles = GetFiles(objectFiles["export"]);
                    Dictionary<string, Asset> deckAssets = new(deckPackage.Count);
                    for (var i = 0; i < deckFiles.Count; i++)
                    {
                        var fileName = GetChildName(deckFiles[i]);
                        var data = deckPackage[i];
                        if (fileName.EndsWith(".hmg"))
                        {
                            deckAssets.Add(fileName, Asset.Create<HmgAsset>(data));
                        }
                        else
                        {
                            deckAssets.Add(fileName, Asset.Create<TextAsset>(data));
                        }
                    }

                    return new PackageAsset(deckAssets);

                case "font_ttf":
                case "font_bmfont":
                    return Asset.Create<TextAsset>(LoadFile(objectFiles["font"]));
                case "shader_script":
                // Since we cannot compile/decompile shaders...
                case "glsl":
                    return Asset.Create<TextAsset>(LoadFile(objectFiles["src"]));
                case "texture":
                    if (objectFiles.Count == 0)
                    {
                        // TODO TextureIndex
                        return null;
                    }

                    if (fileType == "v" && assetName.EndsWith("_texture"))
                    {
                        PackageAsset parent =
                            (PackageAsset)_assets[Path.GetDirectoryName(assetName)!.Replace('\\', '/')];
                        parent.AddChild("atlas", Asset.Create<HmgAsset>(LoadFile(objectFiles["pixmap"])));
                        return null;
                    }

                    return Asset.Create<HmgAsset>(LoadFile(objectFiles["pixmap"]));
                case "fs_project":
                case "fs_event":
                case "fs_folder":
                    // TODO Audio Handling
                    return null;
                case "lua":
                    // TODO Implement lua
                    return Asset.Create<BinaryAsset>(LoadFile(_scriptLibrary[assetName]));
                case "data_json":
                case "data_csv":
                case "data_xls":
                case "animator_data":
                case "text":
                case "code_tileset":
                case "asset_map":
                case "multi_texture":
                case "tb_scheme":
                    return Asset.Create<TextAsset>(LoadFile(objectFiles["data"]));
                case "locale_pack":
                    var localePackage = LoadDirectory(objectFiles["data"]);
                    var localeFiles = GetFiles(objectFiles["data"]);
                    Dictionary<string, Asset> localeAssets = new(localePackage.Count);
                    for (var i = 0; i < localePackage.Count; i++)
                    {
                        var fileName = GetChildName(localeFiles[i]);
                        var data = localePackage[i];
                        localeAssets.Add(fileName, Asset.Create<TextAsset>(data));
                    }

                    return new PackageAsset(localeAssets);
                case "raw":
                case "movie":
                    return Asset.Create<BinaryAsset>(LoadFile(objectFiles["data"]));
                case "sq_script":
                    return Asset.Create<TextAsset>(LoadFile(objectFiles["data"]));

                case "msprite":
                    return new PackageAsset(new Dictionary<string, Asset>(1)
                    {
                        { "def", Asset.Create<TextAsset>(LoadFile(objectFiles["def"])) }
                    });
                case "proto":
                    return Asset.Create<TextAsset>(LoadFile(objectFiles["def"]));

                case "effect":
                case "fsm_scheme":
                case "bt_script":
                case "material":
                case "physics_body_def":
                case "physics_material":
                case "scene_portal_graph":
                case "quest_scheme":
                case "stylesheet":
                case "prefab":
                case "story_graph":
                case "render_target":
                case "ui_style":
                case "deck2d":
                    return Asset.Create<TextAsset>(LoadFile(objectFiles["def"]));

                case "scene":
                    if (fileType == "d")
                    {
                        var scenePackage = LoadDirectory(objectFiles["def"]);
                        var sceneFiles = GetFiles(objectFiles["def"]);
                        Dictionary<string, Asset> sceneAssets = new(scenePackage.Count);
                        for (var i = 0; i < scenePackage.Count; i++)
                        {
                            var data = scenePackage[i];
                            var sceneFile = GetChildName(sceneFiles[i]);
                            sceneAssets.Add(sceneFile, Asset.Create<TextAsset>(data));
                        }

                        return new PackageAsset(sceneAssets);
                    }

                    return new PackageAsset(new Dictionary<string, Asset>(1)
                    {
                        { "default.scene_group", Asset.Create<TextAsset>(LoadFile(objectFiles["def"])) }
                    });

                case "mesh":
                    var meshPackage = LoadDirectory(objectFiles["mesh"]);
                    var meshFiles = GetFiles(objectFiles["mesh"]);
                    Dictionary<string, Asset> meshAssets = new(meshPackage.Count);
                    for (var i = 0; i < meshFiles.Count; i++)
                    {
                        var fileName = GetChildName(meshFiles[i]);
                        var data = meshPackage[i];
                        if (fileName.EndsWith(".hmg"))
                        {
                            meshAssets.Add(fileName, Asset.Create<HmgAsset>(data));
                        }
                        else
                        {
                            meshAssets.Add(fileName, Asset.Create<TextAsset>(data));
                        }
                    }

                    return new PackageAsset(meshAssets);
                case "com_script":
                    return Asset.Create<TextAsset>(LoadFile(objectFiles["script"]));
                case "lut_texture":
                    return Asset.Create<BinaryAsset>(LoadFile(objectFiles["texture"]));
            }
        }
        catch (ArchiveNotFoundException)
        {
            // Ignored
        }
        catch (Exception e)
        {
            Console.WriteLine($"{e} at {assetName}");
        }

        return null;
    }

    private byte[] LoadFile(string virtualPath)
    {
        var split = virtualPath.Split('/');
        var archiveName = split[0];
        if (!_archives.ContainsKey(archiveName))
        {
            throw new ArchiveNotFoundException($"Archive name with {archiveName} Not Loaded!!!");
        }

        return _archives[archiveName][virtualPath];
    }

    private List<byte[]> LoadDirectory(string virtualPath)
    {
        var split = virtualPath.Split('/');
        var archiveName = split[0];
        if (!_archives.ContainsKey(archiveName))
        {
            throw new ArchiveNotFoundException($"Archive name with {archiveName} Not Loaded!!!");
        }

        List<byte[]> results = new List<byte[]>();
        foreach (var (key, value) in _archives[archiveName])
        {
            if (key.StartsWith(virtualPath))
            {
                results.Add(value);
            }
        }

        return results;
    }

    private List<string> GetFiles(string virtualPath)
    {
        var split = virtualPath.Split('/');
        var archiveName = split[0];
        if (!_archives.ContainsKey(archiveName))
        {
            throw new Exception($"Archive name with {archiveName} Not Loaded!!!");
        }

        List<string> results = new List<string>();
        foreach (var name in _archives[archiveName].Keys)
        {
            if (name.StartsWith(virtualPath))
            {
                results.Add(name);
            }
        }

        return results;
    }
    
    private string GetChildName(string objectPath)
    {
        var split = objectPath.Split('/');
        var archiveName = split[0];
        var guid = split[1];
        return objectPath.Substring(guid.Length + archiveName.Length + 2);
    }

    public void ExtractTo(string path)
    {
        foreach (var (assetName, asset) in _assets)
        {
            asset.SaveTo(Path.Combine(path, assetName));
        }
    }
}