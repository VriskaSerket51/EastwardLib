using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EastwardLib.Assets;
using EastwardLib.MetaData;
using EastwardLib.Utils;

namespace EastwardLib.Compilation;

public sealed class AssetCompiler(AssetManager manager)
{
    private static readonly Dictionary<string, string> SourceRoles = new(StringComparer.Ordinal)
    {
        ["bt_script"] = "def",
        ["material"] = "def",
        ["physics_body_def"] = "def",
        ["physics_material"] = "def",
        ["effect"] = "def",
        ["prefab"] = "def",
        ["render_target"] = "def",
        ["ui_style"] = "def",
        ["stylesheet"] = "def",
        ["deck2d"] = "def",
        ["glsl"] = "src",
        ["shader_script"] = "src",
        ["com_script"] = "script",
        ["data_json"] = "data",
        ["animator_data"] = "data",
        ["raw"] = "data",
        ["tb_scheme"] = "data",
        ["font_ttf"] = "font",
        ["font_bmfont"] = "font",
        ["movie"] = "data",
        ["text"] = "data",
        ["asset_map"] = "data",
        ["code_tileset"] = "data",
        ["multi_texture"] = "data"
    };

    public static bool Supports(string type)
    {
        return SourceRoles.ContainsKey(type) || type is "texture" or "lut_texture" or "proto" or "msprite" or "fsm_scheme" or "quest_scheme" or "scene_portal_graph" or "story_graph" or "mesh" or "sq_script" or "data_csv" or "data_xls" or "scene" or "locale_pack" or "lua" or "texture_pack" or "named_tileset_pack" or "deck_pack" or "deck_pack_raw" or "fs_project";
    }

    /// <summary>Compile one existing indexed asset without changing loaded archives or metadata.</summary>
    public IReadOnlyDictionary<string, byte[]> Compile(string name, string sourcePath)
    {
        return Compile(new Dictionary<string, string>(StringComparer.Ordinal) { [name] = sourcePath });
    }

    /// <summary>Compile an edited asset held by an editor without writing temporary source files.</summary>
    public IReadOnlyDictionary<string, byte[]> Compile(string name, Asset edited)
    {
        return Compile(new Dictionary<string, Asset>(StringComparer.Ordinal) { [name] = edited });
    }

    /// <summary>Compile edits together so shared atlas updates retain every replacement.</summary>
    public IReadOnlyDictionary<string, byte[]> Compile(IReadOnlyDictionary<string, string> sources, IReadOnlyDictionary<string, string>? sourceRemappings = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return Compile(sources.Select(pair =>
        {
            if (!File.Exists(pair.Value) && !Directory.Exists(pair.Value))
            {
                throw new FileNotFoundException("Missing edited source", pair.Value);
            }
            var original = manager.LoadAsset(pair.Key) ?? throw new InvalidDataException("Missing asset: " + pair.Key);
            return new KeyValuePair<string, Asset>(pair.Key, original.CompileSource(pair.Value));
        }), sourceRemappings);
    }

    /// <summary>Compile a batch of edited assets, retaining every shared atlas replacement.</summary>
    public IReadOnlyDictionary<string, byte[]> Compile(IEnumerable<KeyValuePair<string, Asset>> assets, IReadOnlyDictionary<string, string>? sourceRemappings = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var output = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var references = new SourceReference(sourceRemappings ?? new Dictionary<string, string>());
        foreach (var (name, edited) in assets)
        {
            ArgumentNullException.ThrowIfNull(edited);
            if (!names.Add(name))
            {
                throw new ArgumentException("Duplicate edited asset: " + name, nameof(assets));
            }

            var info = manager.Assets[name];
            var original = manager.LoadAsset(name) ?? throw new InvalidDataException("Missing asset: " + name);
            JsonNode? Rewrite(JsonNode? data) => data == null ? null : JsonNode.Parse(references.RewritePrototypes(references.Rewrite(data.ToJsonString())));
            if (SourceRoles.TryGetValue(info.Type, out var role))
            {
                var bytes = edited switch
                {
                    JsonAsset json => new JsonAsset(Rewrite(json.Data)).Encode(),
                    TextAsset text => Encoding.UTF8.GetBytes(references.Rewrite(text.Text)),
                    _ => edited.Encode()
                };
                Put(output, Role(info, role), bytes);
                continue;
            }
            switch (info.Type)
            {
                case "texture":
                    CompileTexture(info, ((HmgAsset)edited).Image!, output);
                    break;
                case "lut_texture":
                    Put(output, Role(info, "texture"), edited.Encode());
                    break;
                case "proto":
                    PutDefinition(info, Rewrite(((JsonAsset)edited).Data), output);
                    break;
                case "msprite":
                    CompileSprite(info, (MSpriteAsset)edited, output, references);
                    break;
                case "fsm_scheme":
                case "quest_scheme":
                case "scene_portal_graph":
                case "story_graph":
                    Put(output, Role(info, "def"), Encoding.UTF8.GetBytes(references.Rewrite(((TextAsset)edited).Text)));
                    break;
                case "mesh":
                    var mesh = (MeshAsset)edited;
                    Put(output, Role(info, "mesh") + "/mesh", Encoding.UTF8.GetBytes(mesh.MeshData));
                    foreach (var (path, bytes) in mesh.TextureFiles)
                    {
                        Put(output, Role(info, "mesh") + "/" + path, bytes);
                    }

                    break;
                case "sq_script":
                case "data_csv":
                    PutData(info, references.RewriteData(((JsonAsset)edited).Data), output);
                    break;
                case "data_xls":
                    var workbook = (WorkbookAsset)edited;
                    if (!((WorkbookAsset)original).Data!.AsObject().Select(pair => pair.Key).Order().SequenceEqual(workbook.Data!.AsObject().Select(pair => pair.Key).Order()))
                    {
                        throw new NotSupportedException("Keep existing sheet names; new virtual sheets require index registration");
                    }

                    PutData(info, references.RewriteData(workbook.Data), output);
                    Put(output, Role(info, "meta_data"), new JsonAsset(workbook.Metadata).Encode());
                    break;
                case "scene":
                    CompileScene(info, edited, (SceneAsset)original, output, references);
                    break;
                case "locale_pack":
                    var locale = (LocalePackAsset)edited;
                    Put(output, Role(info, "config"), new JsonAsset(locale.Config).Encode());
                    foreach (var (language, table) in locale.Languages)
                    {
                        Put(output, Role(info, "data") + "/" + language, Encoding.UTF8.GetBytes("return " + LocaleCompiler.WriteLua(table)));
                    }

                    break;
                case "lua":
                    var export = manager.Scripts.GetValueOrDefault(info.AssetName) ?? throw new InvalidDataException("Missing script export: " + name);
                    var lua = (LuaAsset)edited;
                    Put(output, export, lua.IsBytecode ? lua.Encode() : Encoding.UTF8.GetBytes(references.Rewrite(lua.GetSource())));
                    break;
                case "texture_pack":
                    foreach (var child in manager.GetChildren(info.AssetName).Where(child => child.Type == "texture"))
                    {
                        CompileTexture(child, ((TexturePackAsset)edited).Textures[child.Name], output);
                    }

                    break;
                case "named_tileset_pack":
                    PutImage(output, Role(info, "atlas"), ((HmgAsset)((TilesetPackAsset)edited).Items["atlas.png"]).Image!);
                    break;
                case "deck_pack":
                case "deck_pack_raw":
                    foreach (var (path, item) in ((PackageAsset)edited).Items)
                    {
                        if (item is HmgAsset image)
                        {
                            PutImage(output, Role(info, "export") + "/" + Path.ChangeExtension(path, ".hmg"), image.Image!);
                        }
                        else
                        {
                            Put(output, Role(info, "export") + "/" + path, item is JsonAsset json ? new JsonAsset(Rewrite(json.Data)).Encode() : item.Encode());
                        }
                    }
                    break;
                case "fs_project":
                    foreach (var (path, item) in ((PackageAsset)edited).Items)
                    {
                        Put(output, Role(info, "data") + "/" + path, item.Encode());
                    }

                    break;
                default:
                    throw new NotSupportedException("Source compilation is not supported for asset type: " + info.Type);
            }
        }
        return output;
    }

    public IReadOnlyDictionary<string, byte[]> CompileProject(string sourceDirectory, Func<AssetInfo, bool>? filter = null)
    {
        var report = ReadJson(Path.GetFullPath(sourceDirectory) + ".recovery.json");
        if (report["format"]?.ToString() != "eastward-source-restoration")
        {
            throw new InvalidDataException("Invalid source recovery report");
        }

        var mappings = (report["pathRemappings"] as JsonObject ?? []).ToDictionary(pair => pair.Value!.ToString(), pair => pair.Key, StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, entry) in report["restored"]!.AsObject())
        {
            var info = manager.Assets[name];
            if (info.Type == "folder" || info.FilePath == null || filter != null && !filter(info))
            {
                continue;
            }

            sources.Add(name, SafePath.ResolveWithin(sourceDirectory, entry!["path"]!.ToString()));
        }
        return Compile(sources, mappings);
    }

    /// <summary>Write full copies of affected archives; original entries and compression modes are retained.</summary>
    public void SaveTo(string destination, IReadOnlyDictionary<string, byte[]> resources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Count == 0)
        {
            throw new ArgumentException("No compiled resources to save", nameof(resources));
        }
        var root = Path.GetFullPath(destination);
        if (!string.IsNullOrEmpty(manager.RootDirectory))
        {
            var input = Path.GetFullPath(manager.RootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (root.Equals(input, StringComparison.OrdinalIgnoreCase) || root.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Compiled archives must be outside the game resource directory");
            }
        }
        if (Directory.Exists(root) || File.Exists(root))
        {
            throw new IOException("Use a fresh compilation output directory");
        }

        var archives = new Dictionary<string, GArchive>(StringComparer.Ordinal);
        foreach (var (path, bytes) in resources)
        {
            ValidatePath(path);
            var archiveName = path[..path.IndexOf('/')];
            if (!archives.TryGetValue(archiveName, out var archive))
            {
                var original = manager.GetLoadedArchive(archiveName) ?? throw new InvalidOperationException("Load the target archive before compiling: " + archiveName);
                archive = original.CopyForWriting();
                archives.Add(archiveName, archive);
            }
            archive[path] = bytes;
        }
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)) ?? throw new ArgumentException("An output directory is required", nameof(destination));
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".eastward-compile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var archive in archives.Values)
            {
                archive.Write(SafePath.ResolveWithin(temporary, archive.ArchiveName + ".g"));
            }
            Directory.Move(temporary, root);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                foreach (var file in Directory.EnumerateFiles(temporary))
                {
                    File.Delete(file);
                }
                Directory.Delete(temporary);
            }
        }
    }

    /// <summary>Compile the edited indices without changing the original configuration archive.</summary>
    public IReadOnlyDictionary<string, byte[]> CompileMetadata(string archiveName = "config")
    {
        var original = manager.GetLoadedArchive(archiveName) ?? throw new InvalidOperationException("Load the configuration archive first: " + archiveName);
        var copy = original.CopyForWriting();
        manager.SaveMetadata(copy);
        var prefix = archiveName + "/";
        return new[] { "asset_index", "asset_index.packed", "script_library", "texture_index" }
            .Select(name => prefix + name).Where(copy.ContainsKey)
            .ToDictionary(path => path, path => copy[path], StringComparer.Ordinal);
    }

    private static string Role(AssetInfo info, string role)
    {
        return info.ObjectFiles.GetValueOrDefault(role) ?? throw new InvalidDataException("Missing " + role + " resource for " + info.AssetName);
    }

    private static void ValidatePath(string path)
    {
        var parts = path.Split('/');
        if (parts.Length < 2 || parts.Any(part => part is "" or "." or ".." || part.Contains('\\') || part.Contains(':') || part.Contains('\0')))
        {
            throw new InvalidDataException("Invalid runtime resource path: " + path);
        }
    }

    private static void Put(Dictionary<string, byte[]> output, string path, byte[] bytes)
    {
        ValidatePath(path);
        if (output.TryGetValue(path, out var previous) && !previous.AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidDataException("Conflicting compiled resources: " + path);
        }

        output[path] = bytes;
    }

    private byte[] Original(string path, Dictionary<string, byte[]> output)
    {
        return output.GetValueOrDefault(path) ?? manager.RequireFile(path);
    }

    private void PutData(AssetInfo info, JsonNode? data, Dictionary<string, byte[]> output)
    {
        if (info.ObjectFiles.TryGetValue("data", out var path))
        {
            Put(output, path, new JsonAsset(data).Encode());
        }

        if (info.ObjectFiles.TryGetValue("packed_data", out var packed))
        {
            Put(output, packed, new JsonAsset(data, true).Encode());
        }

        if (!info.ObjectFiles.ContainsKey("data") && !info.ObjectFiles.ContainsKey("packed_data"))
        {
            throw new InvalidDataException("Missing data resource");
        }
    }

    private static void PutDefinition(AssetInfo info, JsonNode? data, Dictionary<string, byte[]> output)
    {
        if (info.ObjectFiles.TryGetValue("def", out var path))
        {
            Put(output, path, new JsonAsset(data).Encode());
        }

        if (info.ObjectFiles.TryGetValue("packed_def", out var packed))
        {
            Put(output, packed, new JsonAsset(data, true).Encode());
        }

        if (!info.ObjectFiles.ContainsKey("def") && !info.ObjectFiles.ContainsKey("packed_def"))
        {
            throw new InvalidDataException("Missing definition resource");
        }
    }

    private void PutImage(Dictionary<string, byte[]> output, string path, RgbaImage image, bool shared = false)
    {
        var original = Original(path, output);
        var bytes = original.AsSpan().StartsWith("PGF"u8)
            ? new HmgAsset(image).Encode(HmgHeader.Read(original).PaddingA.Span, HmgHeader.Read(original).PaddingB.Span)
            : image.ToPng();
        if (shared)
        {
            output[path] = bytes;
        }
        else
        {
            Put(output, path, bytes);
        }
    }

    private void CompileTexture(AssetInfo info, RgbaImage image, Dictionary<string, byte[]> output)
    {
        manager.Textures.Textures.TryGetValue(info.AssetName, out var texture);
        if (texture?.PremultiplyAlpha == true)
        {
            var pixels = (byte[])image.Pixels.Clone();
            for (var pixel = 0; pixel < pixels.Length; pixel += 4)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    pixels[pixel + channel] = (byte)((pixels[pixel + channel] * pixels[pixel + 3] + 127) / 255);
                }
            }

            image = new RgbaImage(image.Width, image.Height, pixels);
        }
        if (texture?.AtlasMode == true)
        {
            int Number(string key) => int.Parse(texture.Data[key]!.ToString(), CultureInfo.InvariantCulture);
            if (image.Width != Number("w") || image.Height != Number("h"))
            {
                throw new InvalidDataException("Keep the indexed atlas texture dimensions: " + info.AssetName);
            }

            var atlasData = manager.RequireJson(texture.AtlasPath + "/atlas.json")["atlases"]!.AsArray();
            var page = Number("atlasId") - 1;
            if (page < 0 || page >= atlasData.Count)
            {
                throw new InvalidDataException("Invalid atlas page");
            }

            var path = texture.AtlasPath + "/" + atlasData[page]!["name"];
            var atlas = AssetManager.DecodeImage(Original(path, output));
            var x = Number("x");
            var y = Number("y");
            atlas.Crop(x, y, image.Width, image.Height);
            for (var row = 0; row < image.Height; row++)
            {
                image.Pixels.AsSpan(row * image.Width * 4, image.Width * 4).CopyTo(atlas.Pixels.AsSpan(((y + row) * atlas.Width + x) * 4));
            }

            PutImage(output, path, atlas, true);
        }
        else
        {
            PutImage(output, Role(info, "pixmap"), image);
        }
    }

    private static JsonNode ReadJson(string path)
    {
        var text = File.ReadAllText(path);
        return JsonNode.Parse(text) ?? throw new InvalidDataException("Empty JSON source: " + path);
    }

    private void CompileScene(AssetInfo info, Asset edited, SceneAsset original, Dictionary<string, byte[]> output, SourceReference references)
    {
        JsonNode Rewrite(JsonNode data) => JsonNode.Parse(references.RewritePrototypes(references.Rewrite(data.ToJsonString())))!;
        void Prototypes(JsonNode data, JsonNode? previous)
        {
            foreach (var proto in data["protos"] as JsonArray ?? [])
            {
                var name = proto!["name"]!.ToString();
                var value = JsonNode.Parse(Convert.FromBase64String(proto["serialized"]!.ToString()));
                var old = (previous?["protos"] as JsonArray)?.FirstOrDefault(item => item?["name"]?.ToString() == name);
                if (old != null && MetadataJson.Equivalent(value, JsonNode.Parse(Convert.FromBase64String(old["serialized"]!.ToString()))))
                {
                    continue;
                }

                var child = manager.GetChildren(info.AssetName).SingleOrDefault(value => value.Type == "proto" && value.Name == name)
                    ?? throw new InvalidDataException("Register new scene prototypes in the asset index: " + name);
                PutDefinition(child, value, output);
            }
        }
        if (edited is JsonAsset single)
        {
            var data = Rewrite(single.Data!);
            Put(output, Role(info, "def"), new JsonAsset(data).Encode());
            Prototypes(data, original.Index);
            return;
        }
        foreach (var (path, item) in ((PackageAsset)edited).Items)
        {
            if (item is JsonAsset json)
            {
                var data = Rewrite(json.Data!);
                Put(output, Role(info, "def") + "/" + path, new JsonAsset(data).Encode());
                Put(output, Role(info, "compiled") + "/" + path + ".packed", new JsonAsset(data, true).Encode());
                Prototypes(data, path == "scene_index.json" ? original.Index : original.Groups.GetValueOrDefault(path));
            }
            else
            {
                Put(output, Role(info, "def") + "/" + path, item.Encode());
            }
        }
    }

    private void CompileSprite(AssetInfo info, MSpriteAsset sprite, Dictionary<string, byte[]> output, SourceReference references)
    {
        PutDefinition(info, references.RewriteData(sprite.Data), output);
        var children = manager.GetChildren(info.AssetName).Where(child => child.Type == "texture").ToArray();
        var atlases = sprite.Data!["atlases"]!.AsObject();
        foreach (var (id, image) in sprite.AtlasImages)
        {
            var path = atlases[id]!.ToString();
            var basename = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
            var matches = children.Where(child => child.Name == basename || child.AssetName == path || child.ObjectFiles.Values.Contains(path)).ToArray();
            var child = matches.Length == 1 ? matches[0] : matches.Length == 0 && atlases.Count == 1 && children.Length == 1 ? children[0]
                : throw new InvalidDataException("Missing or ambiguous sprite atlas: " + id);
            CompileTexture(child, image, output);
        }
    }
}
