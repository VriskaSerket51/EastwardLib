using System.Text.Json;
using EastwardLib.Assets;
using EastwardLib.MetaData;

namespace EastwardLib.Utils;

public sealed class ProjectReconstructor(AssetManager manager)
{
    public sealed record Entry(string Path, string Provenance);

    public sealed class Report
    {
        public string Format { get; } = "eastward-source-restoration";
        public Dictionary<string, Entry> Restored { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Entry> Alternatives { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Unavailable { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Warnings { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> PathRemappings { get; } = new(StringComparer.Ordinal);
        public List<string> RemappedDocuments { get; } = [];
        public int VirtualAssets
        {
            get; set;
        }
        public bool Partial
        {
            get; set;
        }
        public string[] Limitations { get; } = ["Original project settings and engine binaries are unavailable.", "Reconstructed sources may lose editor-only information.", "Full editor loading and native engine builds require the matching Gii/AKU environment."];
    }

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
        ["text"] = "data"
    };

    public Report SaveTo(string destination, Func<AssetInfo, bool>? filter = null, Action<int, int, string>? onProgress = null)
    {
        var root = Path.GetFullPath(destination);
        if (!string.IsNullOrEmpty(manager.RootDirectory))
        {
            var input = Path.GetFullPath(manager.RootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (root.Equals(input, StringComparison.OrdinalIgnoreCase) || root.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Source project must be outside the input directory");
            }
        }
        if (Directory.Exists(root) || File.Exists(root) || File.Exists(root + ".recovery.json"))
        {
            throw new IOException("Use a fresh project output directory");
        }

        var report = new Report();
        var packages = manager.Assets.Values.Where(info => info.Type == "deck_pack" && info.FilePath != null).ToDictionary(info => info.AssetName,
            info => info.AssetName.EndsWith(".decks.psd", StringComparison.OrdinalIgnoreCase) ? info.AssetName[..^10] + ".deckpack" : info.AssetName + ".deckpack", StringComparer.Ordinal);
        string SourcePath(AssetInfo info)
        {
            foreach (var (original, replacement) in packages)
            {
                if (info.AssetName == original || info.AssetName.StartsWith(original + "/", StringComparison.Ordinal))
                {
                    return replacement + info.AssetName[original.Length..];
                }
            }

            return info.AssetName;
        }
        var escaped = ProjectPath.Map(manager.Assets.Values.Select(SourcePath));
        var mapping = manager.Assets.Values.ToDictionary(info => info.AssetName, info => escaped[SourcePath(info)], StringComparer.Ordinal);
        var physical = manager.Assets.Values.Where(info => info.FilePath != null).Select(info => mapping[info.AssetName]).ToArray();
        if (physical.Distinct(StringComparer.OrdinalIgnoreCase).Count() != physical.Length)
        {
            throw new InvalidDataException("Source package remapping collides with an existing asset");
        }
        // Virtual names are importer identifiers, not Windows filenames. Remap their
        // renamed source package prefix while preserving names such as animation tags.
        foreach (var info in manager.Assets.Values.Where(info => info.FilePath != null).OrderBy(info => info.AssetName.Length))
        {
            var original = info.AssetName;
            var replacement = mapping[original];
            if (original == replacement || report.PathRemappings.Any(pair => original.StartsWith(pair.Key + "/", StringComparison.Ordinal) && replacement == pair.Value + original[pair.Key.Length..]))
            {
                continue;
            }

            report.PathRemappings.Add(original, replacement);
        }
        var selected = manager.Assets.Values.Where(info => filter == null || filter(info)).ToArray();
        report.Partial = selected.Length != manager.Assets.Count;
        Directory.CreateDirectory(root);
        var references = new SourceReference(report.PathRemappings);
        var completed = 0;
        foreach (var info in selected)
        {
            if (info.FilePath == null)
            {
                report.VirtualAssets++;
            }
            else
            {
                if (info.FilePath != "game/asset/" + info.AssetName)
                {
                    throw new InvalidDataException("Unexpected indexed source path: " + info.FilePath);
                }

                var relative = "game/asset/" + mapping[info.AssetName];
                var path = SafePath.ResolveWithin(root, relative);
                try
                {
                    var provenance = File.Exists(path) || info.Type != "folder" && Directory.Exists(path) ? "reconstructed-parent-resource" : SaveAsset(info, path, references);
                    if (provenance == "runtime-image-alternative")
                    {
                        report.Alternatives.Add(info.AssetName, new Entry(relative + ".recovered.png", provenance));
                        report.Unavailable.Add(info.AssetName, "Original compressed image bytes were not deployed; runtime pixels are saved as a PNG alternative.");
                    }
                    else
                    {
                        if (!File.Exists(path) && !Directory.Exists(path))
                        {
                            throw new FileNotFoundException("Source writer did not produce an output");
                        }

                        report.Restored.Add(info.AssetName, new Entry(relative, provenance));
                    }
                    if (info.Type == "deck_pack")
                    {
                        report.Warnings.Add(info.AssetName, "Restored as a Gii raw deckpack; original PSD authoring layers are unavailable.");
                    }

                    if (info.Type == "texture" && !Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        report.Warnings.TryAdd(info.AssetName, "Runtime pixels are recovered; original image editing layers and compressed source bytes are unavailable.");
                    }
                }
                catch (Exception error) when (error is FileNotFoundException or NotSupportedException)
                {
                    report.Unavailable.Add(info.AssetName, error.Message);
                }
            }
            onProgress?.Invoke(++completed, selected.Length, info.AssetName);
        }
        foreach (var entry in report.Restored.Values)
        {
            if (references.RewriteFile(SafePath.ResolveWithin(root, entry.Path)))
            {
                report.RemappedDocuments.Add(entry.Path);
            }
        }

        new TextAsset(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).SaveTo(root + ".recovery.json");
        return report;
    }

    private string SaveAsset(AssetInfo info, string path, SourceReference references)
    {
        if (info.Type == "folder")
        {
            Directory.CreateDirectory(path);
            return "indexed-directory";
        }
        if (SourceRoles.TryGetValue(info.Type, out var role))
        {
            new BinaryAsset(manager.RequireFile(info.ObjectFiles.GetValueOrDefault(role))).SaveTo(path);
            return "original-bytes";
        }
        var asset = manager.LoadAsset(info.AssetName) ?? throw new FileNotFoundException("Resource was not deployed");
        switch (asset)
        {
            case WorkbookAsset workbook:
                new WorkbookAsset(references.RewriteData(workbook.Data)!, workbook.Metadata).SaveTo(path);
                return "reconstructed-data_xls";
            case LuaAsset lua:
                lua.SaveSourceTo(path);
                return "decompiled";
            case GraphAsset graph:
                graph.SaveSourceTo(path);
                new TextAsset(graph.Text).SaveTo(path + (info.Type == "fsm_scheme" ? ".runtime.lua" : ".runtime.json"));
                return "reconstructed-graph-topology";
            case MSpriteAsset sprite:
                sprite.SaveSourceTo(path);
                return "reconstructed-ase-parts-and-timing";
            case HmgAsset image when Path.GetExtension(path).Equals(".psd", StringComparison.OrdinalIgnoreCase):
                new BinaryAsset(PsdWriter.Write(image.Image!)).SaveTo(path);
                return "flattened-runtime-image";
            case HmgAsset image:
                if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    image.Image!.SavePng(path + ".recovered.png");
                    return "runtime-image-alternative";
                }
                image.SaveTo(path);
                return "decoded-runtime-pixels";
            case BinaryAsset bytes when info.Type == "lut_texture":
                new BinaryAsset(PsdWriter.Write(AssetManager.DecodeImage(bytes.Encode()))).SaveTo(path);
                return "flattened-runtime-lut";
            default:
                asset.SaveTo(path);
                return info.Type == "fs_project" ? "original-built-resources" : "reconstructed-" + info.Type;
        }
    }
}
