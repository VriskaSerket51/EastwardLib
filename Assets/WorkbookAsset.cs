using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class WorkbookAsset : JsonAsset
{
    public JsonNode Metadata
    {
        get;
    }

    public WorkbookAsset(JsonNode data, JsonNode metadata) : base(data)
    {
        Metadata = metadata.DeepClone();
    }

    public IReadOnlyList<IReadOnlyList<JsonNode?>> GetRows(string name)
    {
        var data = Data![name]!;
        var meta = Metadata[name] ?? throw new InvalidDataException("Missing sheet metadata: " + name);
        var type = meta["type"]!.ToString();
        var rows = new List<IReadOnlyList<JsonNode?>>();
        if (type is "list" or "vlist")
        {
            var keys = meta["keys"]!.AsArray().Select(key => key!.ToString()).ToArray();
            var headers = keys.Select((key, i) => (JsonNode?)JsonValue.Create(meta["keyMeta"]?[i]?.ToString() == "localizable" ? "%" + key : key)).ToArray();
            rows.Add(headers);
            rows.AddRange(data.AsArray().Select(row => (IReadOnlyList<JsonNode?>)keys.Select(key => row![key]).ToArray()));
            if (type == "vlist")
            {
                rows = Enumerable.Range(0, keys.Length).Select(column => (IReadOnlyList<JsonNode?>)rows.Select(row => row[column]).ToArray()).ToList();
            }
        }
        else if (type == "dict")
        {
            var hasComments = meta["comment"] is JsonObject comments && comments.Any(pair => pair.Value != null);
            rows.AddRange(meta["sequence"]!.AsArray().Select(key => (IReadOnlyList<JsonNode?>)(hasComments
                ? new JsonNode?[] { key, data[key!.ToString()], meta["comment"]?[key.ToString()] }
                : new JsonNode?[] { key, data[key!.ToString()] })));
        }
        else if (type == "raw")
        {
            rows.AddRange(data.AsArray().Select(row => (IReadOnlyList<JsonNode?>)row!.AsArray().ToArray()));
        }
        else
        {
            throw new NotSupportedException("Unsupported sheet type: " + type);
        }

        return rows;
    }

    /// <summary>Write portable OOXML with original sheet types, order and localization headers.</summary>
    public override void SaveTo(string path)
    {
        path = SafePath.PrepareFile(path);
        if (Path.GetExtension(path).Equals(".xls", StringComparison.OrdinalIgnoreCase))
        {
            var sourceSheets = Data!.AsObject().Select(pair => new EastwardLib.Utils.BiffWorkbook.Sheet(pair.Key + "@" + Metadata[pair.Key]!["type"], GetRows(pair.Key))).ToArray();
            File.WriteAllBytes(path, EastwardLib.Utils.BiffWorkbook.Write(sourceSheets));
            return;
        }
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        XNamespace sheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relationNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace typesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
        var names = Data!.AsObject().Select(pair => pair.Key).ToArray();
        var sheets = new XElement(sheetNs + "sheets");
        var relations = new XElement(packageNs + "Relationships");
        var types = new XElement(typesNs + "Types",
            new XElement(typesNs + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(typesNs + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            new XElement(typesNs + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")));
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            var id = "rId" + (i + 1);
            var file = "worksheets/sheet" + (i + 1) + ".xml";
            sheets.Add(new XElement(sheetNs + "sheet", new XAttribute("name", name + "@" + Metadata[name]!["type"]), new XAttribute("sheetId", i + 1), new XAttribute(relationNs + "id", id)));
            relations.Add(new XElement(packageNs + "Relationship", new XAttribute("Id", id), new XAttribute("Type", relationNs.NamespaceName + "/worksheet"), new XAttribute("Target", file)));
            types.Add(new XElement(typesNs + "Override", new XAttribute("PartName", "/xl/" + file), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
            var rows = new XElement(sheetNs + "sheetData");
            foreach (var row in GetRows(name))
            {
                var cells = new XElement(sheetNs + "row");
                foreach (var value in row)
                {
                    if (value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag))
                    {
                        cells.Add(new XElement(sheetNs + "c", new XAttribute("t", "b"), new XElement(sheetNs + "v", flag ? "1" : "0")));
                    }
                    else if (value is JsonValue number && number.TryGetValue<double>(out _))
                    {
                        cells.Add(new XElement(sheetNs + "c", new XElement(sheetNs + "v", value.ToJsonString())));
                    }
                    else
                    {
                        cells.Add(new XElement(sheetNs + "c", new XAttribute("t", "inlineStr"), new XElement(sheetNs + "is", new XElement(sheetNs + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), EscapeText(value?.ToString() ?? "")))));
                    }
                }
                rows.Add(cells);
            }
            Write(zip, "xl/" + file, new XElement(sheetNs + "worksheet", rows));
        }
        Write(zip, "xl/workbook.xml", new XElement(sheetNs + "workbook", sheets));
        Write(zip, "xl/_rels/workbook.xml.rels", relations);
        Write(zip, "[Content_Types].xml", types);
        Write(zip, "_rels/.rels", new XElement(packageNs + "Relationships", new XElement(packageNs + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", relationNs.NamespaceName + "/officeDocument"), new XAttribute("Target", "xl/workbook.xml"))));
    }

    private static void Write(ZipArchive archive, string path, XElement element)
    {
        using var output = archive.CreateEntry(path).Open();
        new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), element).Save(output);
    }

    private static string EscapeText(string value)
    {
        return Regex.Replace(value, @"_x[0-9a-fA-F]{4}_|[\x00-\x08\x0B\x0C\x0E-\x1F\uFFFE\uFFFF]", match =>
            match.Length > 1 ? "_x005F_" + match.Value[1..] : "_x" + ((int)match.Value[0]).ToString("X4", CultureInfo.InvariantCulture) + "_");
    }
}
