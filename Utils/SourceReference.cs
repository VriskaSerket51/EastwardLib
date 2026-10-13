using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EastwardLib.Utils;

public sealed class SourceReference
{
    private readonly IReadOnlyDictionary<string, string> _mappings;
    private readonly Regex? _pattern;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> Extensions = new(".lua .json .scene .scene_group .prefab .proto .sq .csv .assetmap .asset_map .material .effect .ui .stylesheet .scheme .bt .bt_script .com_script .shader_script .glsl .fsh .vsh .ui_style .render_target .tb_scheme .txt".Split(' '), StringComparer.OrdinalIgnoreCase);

    public SourceReference(IReadOnlyDictionary<string, string> mappings)
    {
        _mappings = mappings;
        if (mappings.Count > 0)
        {
            _pattern = new Regex("(?<![\\w./-])((?:game/asset/)?)(" + string.Join('|', mappings.Keys.OrderByDescending(name => name.Length).Select(Regex.Escape)) + ")(?=/|[\\s\"'`\\]})>,;:]|$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(30));
        }
    }

    public string Rewrite(string text)
    {
        return _pattern?.Replace(text, match => match.Groups[1].Value + _mappings[match.Groups[2].Value]) ?? text;
    }

    public JsonNode? RewriteData(JsonNode? value)
    {
        if (value is JsonObject obj)
        {
            return new JsonObject(obj.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, RewriteData(pair.Value))));
        }

        if (value is JsonArray array)
        {
            return new JsonArray(array.Select(RewriteData).ToArray());
        }

        return value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? JsonValue.Create(Rewrite(text)) : value?.DeepClone();
    }

    public bool RewriteFile(string path)
    {
        if (_pattern == null)
        {
            return false;
        }

        if (Directory.Exists(path))
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(RewriteFile).ToArray().Any(changed => changed);
        }

        var name = Path.GetFileName(path);
        if (name == "runtime-definition.json" || name.EndsWith(".runtime.json", StringComparison.Ordinal) || name.EndsWith(".runtime.lua", StringComparison.Ordinal))
        {
            return false;
        }

        if (Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return RewriteWorkbook(path);
        }

        if (!Extensions.Contains(Path.GetExtension(path)))
        {
            return false;
        }

        string text;
        try
        {
            text = Utf8.GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException) { return false; }
        var rewritten = Rewrite(text);
        if (Path.GetExtension(path) is ".scene" or ".scene_group")
        {
            rewritten = RewritePrototypes(rewritten);
        }

        if (rewritten == text)
        {
            return false;
        }

        File.WriteAllText(path, rewritten, Utf8);
        return true;
    }

    internal string RewritePrototypes(string text)
    {
        JsonNode? data;
        try
        {
            data = JsonNode.Parse(text);
        }
        catch (JsonException) { return text; }
        if (data is not JsonObject || data["protos"] is not JsonArray protos)
        {
            return text;
        }

        var changed = false;
        foreach (var proto in protos.OfType<JsonObject>())
        {
            if (proto["serialized"] is not JsonValue value || !value.TryGetValue<string>(out var encoded))
            {
                continue;
            }

            try
            {
                var source = Utf8.GetString(Convert.FromBase64String(encoded));
                JsonNode.Parse(source);
                var rewritten = Rewrite(source);
                if (source == rewritten)
                {
                    continue;
                }

                proto["serialized"] = Convert.ToBase64String(Utf8.GetBytes(rewritten));
                changed = true;
            }
            catch (Exception error) when (error is FormatException or DecoderFallbackException or JsonException) { }
        }
        return changed ? data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : text;
    }

    private bool RewriteWorkbook(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Update);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var changed = false;
        foreach (var entry in zip.Entries.Where(entry => entry.FullName == "xl/sharedStrings.xml" || entry.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal)).ToArray())
        {
            XDocument document;
            using (var input = entry.Open())
            {
                document = XDocument.Load(input);
            }

            var dirty = false;
            foreach (var item in document.Descendants(ns + "t"))
            {
                var text = Rewrite(item.Value);
                if (text != item.Value)
                {
                    item.Value = text;
                    dirty = true;
                }
            }
            if (!dirty)
            {
                continue;
            }

            var filename = entry.FullName;
            entry.Delete();
            using var output = zip.CreateEntry(filename).Open();
            document.Save(output);
            changed = true;
        }
        return changed;
    }
}
