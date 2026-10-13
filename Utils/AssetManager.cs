using System.Text;
using System.Text.Json.Nodes;
using EastwardLib.Assets;
using EastwardLib.MetaData;
using MessagePack;

namespace EastwardLib.Utils;

public class AssetManager
{
    private readonly AssetIndex _assetIndex;
    private readonly ScriptLibrary _scriptLibrary;
    private readonly TextureIndex _textureIndex;
    private readonly Dictionary<string, GArchive> _archives = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _archiveSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Asset> _assets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<AssetInfo, Asset?>> _loaders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<AssetInfo, Asset?>> _customLoaders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RgbaImage[]> _atlases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private Dictionary<string, AssetInfo[]> _children = new(StringComparer.Ordinal);
    private bool _editingMetadata;

    public string RootDirectory { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, AssetInfo> Assets => _assetIndex;
    public AssetIndex Index => _assetIndex;
    public ScriptLibrary Scripts => _scriptLibrary;
    public TextureIndex Textures => _textureIndex;
    public IReadOnlyCollection<string> SupportedTypes => _loaders.Keys;
    public Func<byte[], string>? LuaDecompiler
    {
        get; set;
    }

    public AssetManager(AssetIndex assetIndex, ScriptLibrary scriptLibrary, TextureIndex textureIndex)
    {
        _assetIndex = assetIndex;
        _scriptLibrary = scriptLibrary;
        _textureIndex = textureIndex;
        _assetIndex.Changed += MetadataChanged;
        _scriptLibrary.Changed += MetadataChanged;
        _textureIndex.Changed += MetadataChanged;
        MetadataChanged();
        RegisterDefaultLoaders();
    }

    /// <summary>Create independent metadata and asset caches over the same input archive sources.</summary>
    public AssetManager CopyForEditing()
    {
        var copy = new AssetManager(AssetIndex.Create(_assetIndex.ToJson(false)), ScriptLibrary.Create(_scriptLibrary.ToJson(false)), TextureIndex.Create(_textureIndex.ToJson(false)))
        {
            RootDirectory = RootDirectory,
            LuaDecompiler = LuaDecompiler
        };
        foreach (var (name, archive) in _archives)
        {
            copy._archives.Add(name, archive.CopyForWriting());
        }

        foreach (var (name, sources) in _archiveSources)
        {
            copy._archiveSources.Add(name, [.. sources]);
        }

        foreach (var (type, loader) in _customLoaders)
        {
            copy.RegisterLoader(type, loader);
        }

        return copy;
    }

    private void MetadataChanged()
    {
        if (_editingMetadata)
        {
            return;
        }
        _assetIndex.ValidateRelationships();
        _children = _assetIndex.Values.GroupBy(info => info.Parent).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        UnloadAssets();
    }

    /// <summary>Rebuild lookups after directly editing JSON bodies or nested metadata.</summary>
    public void RefreshMetadata()
    {
        var editing = _editingMetadata;
        _editingMetadata = true;
        try
        {
            _assetIndex.Refresh();
            _scriptLibrary.Refresh();
            _textureIndex.Refresh();
        }
        finally
        {
            _editingMetadata = editing;
            MetadataChanged();
        }
    }

    /// <summary>Validate a batch of edits before committing it and invalidating loaded resources.</summary>
    public void EditMetadata(Action<AssetIndex, ScriptLibrary, TextureIndex> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (_editingMetadata)
        {
            throw new InvalidOperationException("A metadata edit is already in progress");
        }
        _editingMetadata = true;
        var committed = false;
        try
        {
            var assets = AssetIndex.Create(_assetIndex.ToJson(false));
            var scripts = ScriptLibrary.Create(_scriptLibrary.ToJson(false));
            var textures = TextureIndex.Create(_textureIndex.ToJson(false));
            edit(assets, scripts, textures);
            assets.Refresh();
            scripts.Refresh();
            textures.Refresh();
            MetadataJson.Replace(_assetIndex.Data, assets.Data);
            MetadataJson.Replace(_scriptLibrary.Data, scripts.Data);
            MetadataJson.Replace(_textureIndex.Data, textures.Data);
            committed = true;
            RefreshMetadata();
        }
        finally
        {
            _editingMetadata = false;
            if (committed)
            {
                MetadataChanged();
            }
        }
    }

    public void SaveMetadata(string assetIndexPath, string scriptLibraryPath, string textureIndexPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetIndexPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptLibraryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(textureIndexPath);
        RefreshMetadata();
        // Validate every document before writing any of them.
        var assets = _assetIndex.Encode(assetIndexPath.EndsWith(".packed", StringComparison.OrdinalIgnoreCase));
        var scripts = _scriptLibrary.ToJson();
        var textures = _textureIndex.ToJson();
        var paths = new[] { assetIndexPath, scriptLibraryPath, textureIndexPath }.Select(SafePath.PrepareFile).ToArray();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (paths.Distinct(comparer).Count() != paths.Length)
        {
            throw new ArgumentException("Metadata outputs must have distinct file paths");
        }
        File.WriteAllBytes(paths[0], assets);
        File.WriteAllText(paths[1], scripts);
        File.WriteAllText(paths[2], textures);
    }

    public void SaveMetadata(GArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        RefreshMetadata();
        var assets = Encoding.UTF8.GetBytes(_assetIndex.ToJson());
        var scripts = Encoding.UTF8.GetBytes(_scriptLibrary.ToJson());
        var textures = Encoding.UTF8.GetBytes(_textureIndex.ToJson());
        var prefix = archive.ArchiveName + "/";
        var packedPath = prefix + "asset_index.packed";
        var hasPacked = archive.ContainsKey(packedPath);
        var packed = hasPacked ? _assetIndex.Encode(true) : null;
        if (!hasPacked || archive.ContainsKey(prefix + "asset_index"))
        {
            archive[prefix + "asset_index"] = assets;
        }
        if (packed != null)
        {
            archive[packedPath] = packed;
        }
        archive[prefix + "script_library"] = scripts;
        archive[prefix + "texture_index"] = textures;
    }

    public static AssetManager Create(string assetIndexPath, string scriptLibraryPath, string textureIndexPath)
    {
        var assets = assetIndexPath.EndsWith(".packed", StringComparison.OrdinalIgnoreCase)
            ? AssetIndex.FromPacked(File.ReadAllBytes(assetIndexPath)) : AssetIndex.Create(File.ReadAllText(assetIndexPath));
        return new(assets, ScriptLibrary.Create(File.ReadAllText(scriptLibraryPath)), TextureIndex.Create(File.ReadAllText(textureIndexPath)));
    }

    public static AssetManager Create(string configPath)
    {
        var archive = GArchive.Read(configPath);
        var prefix = archive.ArchiveName + "/";
        var manager = new AssetManager(
            ReadAssetIndex(archive, prefix),
            ScriptLibrary.Create(Encoding.UTF8.GetString(archive[prefix + "script_library"])),
            TextureIndex.Create(Encoding.UTF8.GetString(archive[prefix + "texture_index"])));
        manager.LoadArchive(archive);
        return manager;
    }

    private static AssetIndex ReadAssetIndex(GArchive archive, string prefix)
    {
        var packed = archive.TryGetValue(prefix + "asset_index.packed", out var bytes) ? AssetIndex.FromPacked(bytes) : null;
        if (!archive.TryGetValue(prefix + "asset_index", out var json))
        {
            return packed ?? throw new FileNotFoundException("Archive has no asset index");
        }
        var index = AssetIndex.Create(Encoding.UTF8.GetString(json));
        if (packed != null && !MetadataJson.Equivalent(index.Data, packed.Data))
        {
            throw new InvalidDataException("JSON and packed asset indices disagree");
        }
        // Retain original JSON number spellings when both runtime representations agree.
        return index;
    }

    public void LoadArchive(GArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (GetLoadedArchive(archive.ArchiveName) is GArchive previous)
        {
            previous.AddOverlay(archive);
        }
        else
        {
            _archives.Add(archive.ArchiveName, archive);
        }

        UnloadAssets();
    }

    /// <summary>Register an archive for loading on the first resource request. Later files are overlays.</summary>
    public void RegisterArchive(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Missing archive", path);
        }
        var name = Path.GetFileNameWithoutExtension(path);
        if (!_archiveSources.TryGetValue(name, out var paths))
        {
            paths = [];
            _archiveSources.Add(name, paths);
        }
        paths.Add(path);
        UnloadAssets();
    }

    internal GArchive? GetLoadedArchive(string name)
    {
        if (_archiveSources.TryGetValue(name, out var paths))
        {
            // Parse all pending overlays before committing so a failed read cannot partially apply them.
            GArchive? pending = null;
            foreach (var path in paths)
            {
                var archive = GArchive.Read(path);
                if (pending == null)
                {
                    pending = archive;
                }
                else
                {
                    pending.AddOverlay(archive);
                }
            }
            if (pending != null)
            {
                if (_archives.TryGetValue(name, out var previous))
                {
                    previous.AddOverlay(pending);
                }
                else
                {
                    _archives.Add(name, pending);
                }
            }
            _archiveSources.Remove(name);
        }
        return _archives.GetValueOrDefault(name);
    }

    public void UnloadArchive(string name)
    {
        _archives.Remove(name);
        _archiveSources.Remove(name);
        UnloadAssets();
    }
    public void UnloadArchives()
    {
        _archives.Clear();
        _archiveSources.Clear();
        UnloadAssets();
    }
    public void UnloadAsset(string name)
    {
        _assets.Remove(name);
        _atlases.Clear();
    }
    public void UnloadAssets()
    {
        _assets.Clear();
        _atlases.Clear();
    }
    public IEnumerator<AssetInfo> SearchByType(string type)
    {
        return _assetIndex.Values.Where(info => info.Type == type).GetEnumerator();
    }

    public IReadOnlyList<AssetInfo> GetChildren(string name)
    {
        return _children.GetValueOrDefault(name) ?? [];
    }

    public void RegisterLoader(string type, Func<AssetInfo, Asset?> loader)
    {
        _customLoaders[type] = loader;
        _loaders[type] = loader;
        UnloadAssets();
    }

    public void LoadAssets()
    {
        foreach (var name in _assetIndex.Keys)
        {
            LoadAsset(name);
        }
    }

    public Asset? LoadAsset(string name)
    {
        if (_assets.TryGetValue(name, out var cached))
        {
            return cached;
        }

        if (!_assetIndex.TryGetValue(name, out var info) || info.Type == "folder")
        {
            return null;
        }

        if (!_loading.Add(name))
        {
            throw new InvalidDataException("Cyclic asset reference: " + name);
        }

        try
        {
            var asset = LoadAsset(info);
            if (asset != null)
            {
                _assets[name] = asset;
            }

            return asset;
        }
        finally { _loading.Remove(name); }
    }

    public Asset? LoadAsset(AssetInfo info)
    {
        return _loaders.TryGetValue(info.Type, out var loader)
        ? loader(info) : throw new NotSupportedException("Unsupported asset type: " + info.Type);
    }

    public FileTree GetIndex()
    {
        var tree = new FileTree();
        foreach (var info in _assetIndex.Values.Where(info => info.Type != "folder" && _loaders.ContainsKey(info.Type)))
        {
            tree.AppendFileName(info.AssetName);
        }

        return tree;
    }

    public byte[]? LoadFile(string? virtualPath)
    {
        if (string.IsNullOrEmpty(virtualPath))
        {
            return null;
        }

        var name = virtualPath.Replace('\\', '/');
        if (!string.IsNullOrEmpty(RootDirectory))
        {
            var path = SafePath.ResolveWithin(RootDirectory, name);
            if (File.Exists(path))
            {
                return File.ReadAllBytes(path);
            }
        }
        var separator = name.IndexOf('/');
        return separator >= 0 ? GetLoadedArchive(name[..separator])?.GetValueOrDefault(name) : null;
    }

    public bool FileExists(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var name = path.Replace('\\', '/');
        if (!string.IsNullOrEmpty(RootDirectory) && File.Exists(SafePath.ResolveWithin(RootDirectory, name)))
        {
            return true;
        }

        var separator = name.IndexOf('/');
        return separator >= 0 && GetLoadedArchive(name[..separator]) is GArchive archive && archive.ContainsKey(name);
    }
    public JsonNode? LoadJson(string? path)
    {
        return LoadFile(path) is byte[] bytes ? JsonNode.Parse(bytes, documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip }) : null;
    }

    public JsonNode? LoadPacked(string? path)
    {
        return LoadFile(path) is byte[] bytes ? JsonNode.Parse(MessagePackSerializer.ConvertToJson(bytes)) : null;
    }

    public IReadOnlyList<string> GetFiles(string directory)
    {
        var prefix = directory.Replace('\\', '/').TrimEnd('/') + "/";
        var files = new SortedSet<string>(StringComparer.Ordinal);
        var separator = prefix.IndexOf('/');
        if (GetLoadedArchive(prefix[..separator]) is GArchive archive)
        {
            foreach (var name in archive.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                if (archive[name].Length > 0 || !name.EndsWith('/'))
                {
                    files.Add(name[prefix.Length..]);
                }
            }
        }

        if (!string.IsNullOrEmpty(RootDirectory))
        {
            var path = SafePath.ResolveWithin(RootDirectory, prefix.TrimEnd('/'));
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    files.Add(Path.GetRelativePath(path, file).Replace('\\', '/'));
                }
            }
        }
        return files.ToArray();
    }

    public PackageAsset LoadPackage(string directory, bool decodeImages = false)
    {
        var children = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var name in GetFiles(directory))
        {
            var bytes = RequireFile(directory + "/" + name);
            children.Add(name, decodeImages && bytes.AsSpan().StartsWith("PGF"u8) ? Asset.Create<HmgAsset>(bytes) : new BinaryAsset(bytes));
        }
        return new PackageAsset(children);
    }

    internal byte[] RequireFile(string? path)
    {
        return LoadFile(path) ?? throw new FileNotFoundException("Missing asset resource", path);
    }

    internal JsonNode RequireJson(string? path)
    {
        return LoadJson(path) ?? throw new FileNotFoundException("Missing JSON resource", path);
    }

    internal JsonAsset LoadDefinition(AssetInfo info)
    {
        var packed = info.ObjectFiles.GetValueOrDefault("packed_def");
        var bytes = LoadFile(packed);
        return bytes != null ? JsonAsset.FromPacked(bytes) : new JsonAsset(RequireJson(info.ObjectFiles.GetValueOrDefault("def")));
    }

    private void RegisterDefaultLoaders()
    {
        foreach (var type in new[] { "animator_data", "asset_map", "data_json" })
        {
            _loaders[type] = info => new JsonAsset(RequireJson(info.ObjectFiles.GetValueOrDefault("data")));
        }

        foreach (var type in new[] { "code_tileset", "multi_texture", "tb_scheme" })
        {
            _loaders[type] = info => new TextAsset(Encoding.UTF8.GetString(RequireFile(info.ObjectFiles.GetValueOrDefault("data"))));
        }

        foreach (var type in new[] { "effect", "bt_script", "material", "physics_body_def", "physics_material", "prefab", "quest_scheme", "scene_portal_graph", "story_graph", "render_target", "stylesheet", "ui_style", "deck2d" })
        {
            _loaders[type] = info => new TextAsset(Encoding.UTF8.GetString(RequireFile(info.ObjectFiles.GetValueOrDefault("def"))));
        }

        foreach (var type in new[] { "glsl", "shader_script" })
        {
            _loaders[type] = info => new TextAsset(Encoding.UTF8.GetString(RequireFile(info.ObjectFiles.GetValueOrDefault("src"))));
        }

        foreach (var type in new[] { "raw", "movie" })
        {
            _loaders[type] = info => new BinaryAsset(RequireFile(info.ObjectFiles.GetValueOrDefault("data")));
        }

        foreach (var type in new[] { "file", "texture_processor" })
        {
            _loaders[type] = info => LoadFile(info.ObjectFiles.GetValueOrDefault("data") ?? (info.ObjectFiles.Count == 1 ? info.ObjectFiles.Values.First() : null)) is byte[] bytes ? new BinaryAsset(bytes) : null;
        }

        _loaders["text"] = info => new TextAsset(Encoding.UTF8.GetString(RequireFile(info.ObjectFiles.GetValueOrDefault("data"))));
        _loaders["com_script"] = info => new TextAsset(Encoding.UTF8.GetString(RequireFile(info.ObjectFiles.GetValueOrDefault("script"))));
        _loaders["fsm_scheme"] = info => new TextAsset(Encoding.UTF8.GetString(RequireFile(info.ObjectFiles.GetValueOrDefault("def"))));
        _loaders["font_ttf"] = info => new BinaryAsset(RequireFile(info.ObjectFiles.GetValueOrDefault("font")));
        _loaders["font_bmfont"] = info => new TextAsset(Encoding.UTF8.GetString(RequireFile(info.ObjectFiles.GetValueOrDefault("font"))));
        _loaders["lut_texture"] = info => new BinaryAsset(RequireFile(info.ObjectFiles.GetValueOrDefault("texture")));
        _loaders["proto"] = LoadDefinition;
        foreach (var type in new[] { "fsm_scheme", "quest_scheme", "scene_portal_graph", "story_graph" })
        {
            _loaders[type] = info => new GraphAsset(info.Type, Encoding.UTF8.GetString(RequireFile(info.ObjectFiles.GetValueOrDefault("def"))));
        }

        _loaders["data_csv"] = info => new CsvAsset(RequireJson(info.ObjectFiles.GetValueOrDefault("data")), info.Name);
        _loaders["data_xls"] = info => new WorkbookAsset(RequireJson(info.ObjectFiles.GetValueOrDefault("data")), RequireJson(info.ObjectFiles.GetValueOrDefault("meta_data")));
        _loaders["data_sheet"] = info => new JsonAsset((LoadAsset(info.Parent) as WorkbookAsset)?.Data?[info.Name] ?? throw new InvalidDataException("Missing workbook sheet"));
        _loaders["texture"] = LoadTexture;
        _loaders["texture_pack"] = info => new TexturePackAsset(GetChildren(info.AssetName).Where(child => child.Type == "texture").ToDictionary(child => child.Name, child => ((HmgAsset)LoadAsset(child.AssetName)!).Image!, StringComparer.Ordinal));
        _loaders["scene"] = info => new SceneAsset(this, info);
        _loaders["mesh"] = info => new MeshAsset(this, info);
        _loaders["sq_script"] = info => new SQScriptAsset(RequireJson(info.ObjectFiles.GetValueOrDefault("data")));
        _loaders["lua"] = info => new LuaAsset(RequireFile(_scriptLibrary.GetValueOrDefault(info.AssetName)), bytes =>
            (LuaDecompiler ?? throw new NotSupportedException("Configure AssetManager.LuaDecompiler to extract LuaJIT source"))(bytes));
        _loaders["msprite"] = info => new MSpriteAsset(this, info);
        _loaders["deck2d.msprite_seq"] = info => new JsonAsset(((MSpriteAsset)LoadAsset(info.Parent)!).FindAnimation(info.Name));
        foreach (var type in new[] { "deck_pack", "deck_pack_raw" })
        {
            _loaders[type] = LoadDeckPack;
        }

        foreach (var type in new[] { "deck2d.mquad", "deck2d.mtileset", "deck2d.quad", "deck2d.quads", "deck2d.stretchpatch", "deck2d.tileset", "deck2d.quad_array", "deck2d.polygon" })
        {
            _loaders[type] = LoadDeckItem;
        }

        _loaders["named_tileset_pack"] = LoadTilesetPack;
        _loaders["named_tileset"] = info => new JsonAsset(FindNamed(((PackageAsset)LoadAsset(info.Parent)!).Items["tileset.json"], "themes", info.Name));
        _loaders["locale_pack"] = info => new LocalePackAsset(this, info);
        _loaders["fs_project"] = info => LoadPackage(info.ObjectFiles["data"]);
        _loaders["fs_folder"] = info => new JsonAsset(new JsonArray(GetChildren(info.AssetName).Select(child => (JsonNode)new JsonObject { ["name"] = child.Name, ["path"] = child.AssetName, ["type"] = child.Type }).ToArray()));
        _loaders["fs_event"] = LoadFmodEvent;
    }

    private Asset LoadTexture(AssetInfo info)
    {
        _textureIndex.Textures.TryGetValue(info.AssetName, out var texture);
        RgbaImage image;
        if (texture?.AtlasMode == true && texture.AtlasPath is string path)
        {
            if (!_atlases.TryGetValue(path, out var atlases))
            {
                atlases = RequireJson(path + "/atlas.json")["atlases"]!.AsArray().Select(item => DecodeImage(RequireFile(path + "/" + item!["name"]!.GetValue<string>()))).ToArray();
                _atlases.Add(path, atlases);
            }
            var id = int.Parse(texture.Data["atlasId"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture) - 1;
            if (id < 0 || id >= atlases.Length)
            {
                throw new InvalidDataException("Invalid texture atlas index");
            }

            int Coordinate(string key) => int.Parse(texture.Data[key]!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            image = atlases[id].Crop(Coordinate("x"), Coordinate("y"), Coordinate("w"), Coordinate("h"));
        }
        else
        {
            image = DecodeImage(RequireFile(info.ObjectFiles.GetValueOrDefault("pixmap")));
        }

        return new HmgAsset(texture?.PremultiplyAlpha == true ? image.ToStraightAlpha() : image);
    }

    internal static RgbaImage DecodeImage(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith("PGF"u8))
        {
            return Asset.Create<HmgAsset>(bytes).Image!;
        }

        using var stream = new MemoryStream(bytes);
        return RgbaImage.Load(stream);
    }

    private Asset LoadDeckPack(AssetInfo info)
    {
        var path = info.ObjectFiles["export"];
        var items = new Dictionary<string, Asset>(StringComparer.Ordinal) { ["decks.json"] = new JsonAsset(RequireJson(path + "/decks.json")) };
        foreach (var name in new[] { "decks", "decks_n" })
        {
            if (LoadFile(path + "/" + name + ".hmg") is byte[] bytes)
            {
                items.Add(name + ".png", new HmgAsset(DecodeImage(bytes)));
            }
        }

        return new PackageAsset(items);
    }

    private Asset LoadDeckItem(AssetInfo info)
    {
        var parent = LoadAsset(info.Parent);
        if (parent is PackageAsset pack)
        {
            return new JsonAsset(FindNamed(pack.Items["decks.json"], "decks", info.Name));
        }

        if (parent is TextAsset text)
        {
            var data = JsonNode.Parse(text.Text)!;
            var map = data["map"]!.AsObject();
            var decks = map[data["root"]!.ToString()]!["body"]!["decks"]!.AsArray();
            var item = decks.Select(value => value is JsonObject ? value["body"] ?? value : map[value!.ToString()]?["body"])
                .FirstOrDefault(value => value is JsonObject && value["name"]?.ToString() == info.Name);
            return new JsonAsset(item ?? throw new InvalidDataException("Missing deck item: " + info.AssetName));
        }
        throw new InvalidDataException("Missing parent deck pack");
    }

    private Asset LoadTilesetPack(AssetInfo info)
    {
        return new TilesetPackAsset(RequireJson(info.ObjectFiles["def"]), DecodeImage(RequireFile(info.ObjectFiles["atlas"])));
    }

    private static JsonNode FindNamed(Asset asset, string array, string name)
    {
        return ((JsonAsset)asset).Data![array]!.AsArray().FirstOrDefault(item => item?["name"]?.ToString() == name) ?? throw new InvalidDataException("Missing virtual asset: " + name);
    }

    private Asset LoadFmodEvent(AssetInfo info)
    {
        var parent = info.Parent;
        while (_assetIndex.TryGetValue(parent, out var node))
        {
            if (node.Type == "fs_project")
            {
                var guid = info.Properties?["guid"]?.ToString();
                var path = info.Properties?["path"]?.ToString();
                if (string.IsNullOrEmpty(guid) || path == null || !(path.StartsWith("event:/", StringComparison.Ordinal) || path.StartsWith("snapshot:/", StringComparison.Ordinal)))
                {
                    throw new InvalidDataException("Invalid FMOD event reference");
                }

                return new JsonAsset(new JsonObject { ["guid"] = guid, ["path"] = path, ["project"] = parent });
            }
            parent = node.Parent;
        }
        throw new InvalidDataException("Missing FMOD project");
    }

    public void ExtractTo(string path, bool luaSource = false, Action<string, Exception>? onError = null)
    {
        foreach (var (name, asset) in _assets.ToArray())
        {
            var parent = _assetIndex[name].Parent;
            var embedded = false;
            while (_assetIndex.TryGetValue(parent, out var info))
            {
                if (_assets.TryGetValue(parent, out var container) && (container is not PackageAsset || container is TexturePackAsset))
                {
                    embedded = true;
                    break;
                }
                parent = info.Parent;
            }
            if (embedded)
            {
                continue;
            }

            var output = asset is WorkbookAsset ? Path.ChangeExtension(name, ".xlsx") : asset is MSpriteAsset ? Path.ChangeExtension(name, ".ase") : name;
            try
            {
                var target = SafePath.ResolveWithin(path, output);
                if (luaSource && asset is LuaAsset lua)
                {
                    lua.SaveSourceTo(target);
                }
                else
                {
                    asset.SaveTo(target);
                }
            }
            catch (Exception error)
            {
                if (onError == null)
                {
                    throw;
                }

                onError(name, error);
            }
        }
    }
}
