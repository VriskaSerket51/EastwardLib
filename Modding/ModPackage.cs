using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EastwardLib.Compilation;
using EastwardLib.Utils;

namespace EastwardLib.Modding;

/// <summary>A portable set of resource replacements with their original content hashes.</summary>
public sealed class ModPackage
{
    private readonly Dictionary<string, byte[]> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _originalHashes = new(StringComparer.Ordinal);

    public string Name
    {
        get;
    }
    public IReadOnlyCollection<string> ResourcePaths => _resources.Keys;

    private ModPackage(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public static ModPackage Create(string name, AssetManager manager, IReadOnlyDictionary<string, byte[]> resources)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(resources);
        var package = new ModPackage(name);
        foreach (var (path, bytes) in resources)
        {
            ValidatePath(path);
            var original = manager.LoadFile(path);
            if (original != null && original.AsSpan().SequenceEqual(bytes))
            {
                continue;
            }
            package._resources.Add(path, (byte[])bytes.Clone());
            package._originalHashes.Add(path, original == null ? null : Hash(original));
        }
        if (package._resources.Count == 0)
        {
            throw new ArgumentException("The mod does not change any resources", nameof(resources));
        }
        return package;
    }

    public byte[] GetResource(string path)
    {
        return (byte[])_resources[path].Clone();
    }

    /// <summary>Check every replacement before writing new archives.</summary>
    public IReadOnlyList<string> Validate(AssetManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return _originalHashes.Where(pair =>
        {
            var current = manager.LoadFile(pair.Key);
            return current == null ? pair.Value != null : Hash(current) != pair.Value;
        }).Select(pair => pair.Key).ToArray();
    }

    public void ApplyTo(AssetManager manager, string destination)
    {
        var conflicts = Validate(manager);
        if (conflicts.Count > 0)
        {
            throw new InvalidDataException("Mod resources differ from the original game: " + string.Join(", ", conflicts));
        }
        new AssetCompiler(manager).SaveTo(destination, _resources);
    }

    public void SaveTo(string path, bool overwrite = false)
    {
        path = Path.GetFullPath(path);
        if ((!overwrite && File.Exists(path)) || Directory.Exists(path))
        {
            throw new IOException("Use a new mod package file");
        }
        var manifest = new JsonObject
        {
            ["format"] = "eastward-mod",
            ["version"] = 1,
            ["name"] = Name
        };
        var entries = new JsonObject();
        foreach (var (resource, bytes) in _resources)
        {
            entries.Add(resource, new JsonObject
            {
                ["originalHash"] = _originalHashes[resource],
                ["hash"] = Hash(bytes),
                ["size"] = bytes.Length
            });
        }
        manifest["resources"] = entries;
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".eastward-mod-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                {
                    writer.Write(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                foreach (var (resource, bytes) in _resources)
                {
                    using var output = archive.CreateEntry("resources/" + resource, CompressionLevel.Optimal).Open();
                    output.Write(bytes);
                }
            }
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static ModPackage Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count)
        {
            throw new InvalidDataException("Duplicate mod package entries");
        }
        using var manifestStream = (archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Missing mod manifest")).Open();
        var manifest = JsonNode.Parse(manifestStream) ?? throw new InvalidDataException("Empty mod manifest");
        if (manifest["format"]?.ToString() != "eastward-mod" || manifest["version"]?.GetValue<int>() != 1)
        {
            throw new InvalidDataException("Unsupported mod package format");
        }
        var package = new ModPackage(manifest["name"]?.GetValue<string>() ?? throw new InvalidDataException("Missing mod name"));
        var resources = manifest["resources"] as JsonObject ?? throw new InvalidDataException("Missing mod resources");
        if (resources.Count == 0 || archive.Entries.Count != resources.Count + 1)
        {
            throw new InvalidDataException("Mod package entries do not match the manifest");
        }
        foreach (var (resource, data) in resources)
        {
            ValidatePath(resource);
            var entry = archive.GetEntry("resources/" + resource) ?? throw new InvalidDataException("Missing mod resource: " + resource);
            var size = data?["size"]?.GetValue<int>() ?? throw new InvalidDataException("Missing mod resource size");
            if (size < 0 || entry.Length != size)
            {
                throw new InvalidDataException("Invalid mod resource size: " + resource);
            }
            using var input = entry.Open();
            var bytes = new byte[size];
            input.ReadExactly(bytes);
            if (Hash(bytes) != data?["hash"]?.GetValue<string>())
            {
                throw new InvalidDataException("Mod resource hash differs: " + resource);
            }
            var originalHash = data?["originalHash"]?.GetValue<string>();
            if (originalHash != null && (originalHash.Length != 64 || originalHash.Any(character => !Uri.IsHexDigit(character))))
            {
                throw new InvalidDataException("Invalid original resource hash: " + resource);
            }
            package._resources.Add(resource, bytes);
            package._originalHashes.Add(resource, originalHash);
        }
        return package;
    }

    private static string Hash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void ValidatePath(string path)
    {
        var parts = path.Split('/');
        if (parts.Length < 2 || parts.Any(part => part is "" or "." or ".." || part.Contains('\\') || part.Contains(':') || part.Contains('\0')))
        {
            throw new InvalidDataException("Invalid mod resource path: " + path);
        }
    }
}
