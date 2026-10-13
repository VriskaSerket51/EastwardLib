using System.Text;
using System.Text.Json.Nodes;
using ExcelDataReader;
using EastwardLib.Utils;

namespace EastwardLib.Compilation;

public static class WorkbookCompiler
{
    public sealed record Result(JsonObject Data, JsonObject Metadata);

    public static Result CompileData(JsonNode data, JsonNode originalMetadata)
    {
        var values = data as JsonObject ?? throw new InvalidDataException("Workbook data must contain named sheets");
        var metadata = originalMetadata.DeepClone() as JsonObject ?? throw new InvalidDataException("Workbook metadata must contain named sheets");
        foreach (var (name, sheet) in values)
        {
            var meta = metadata[name] as JsonObject ?? throw new InvalidDataException("Missing sheet metadata: " + name);
            switch (meta["type"]?.ToString())
            {
                case "dict":
                    var dictionary = sheet as JsonObject ?? throw new InvalidDataException("Expected dictionary sheet: " + name);
                    var existing = meta["sequence"] as JsonArray ?? throw new InvalidDataException("Missing dictionary key order: " + name);
                    var keys = existing.Select(key => key!.ToString()).Where(dictionary.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
                    keys.AddRange(dictionary.Select(pair => pair.Key).Except(keys, StringComparer.Ordinal));
                    meta["sequence"] = new JsonArray(keys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray());
                    break;
                case "list":
                case "vlist":
                    if (sheet is not JsonArray rows || rows.Any(row => row is not JsonObject))
                    {
                        throw new InvalidDataException("Expected workbook rows: " + name);
                    }

                    var columns = (meta["keys"] as JsonArray ?? throw new InvalidDataException("Missing workbook headers: " + name)).Select(key => key!.ToString()).ToHashSet(StringComparer.Ordinal);
                    if (rows.Any(row => row!.AsObject().Any(pair => !columns.Contains(pair.Key))))
                    {
                        throw new InvalidDataException("Add new workbook columns through XLS or XLSX source: " + name);
                    }

                    break;
                case "raw":
                    if (sheet is not JsonArray raw || raw.Any(row => row is not JsonArray))
                    {
                        throw new InvalidDataException("Expected raw workbook rows: " + name);
                    }

                    break;
                default:
                    throw new NotSupportedException("Unsupported sheet type: " + name);
            }
        }
        return new Result((JsonObject)values.DeepClone(), metadata);
    }

    public static Result Compile(string path, JsonNode? originalMetadata = null)
    {
        using var stream = File.OpenRead(path);
        return Compile(stream, originalMetadata);
    }

    public static Result Compile(Stream stream, JsonNode? originalMetadata = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration { LeaveOpen = true });
        var data = new JsonObject();
        var metadata = new JsonObject();
        do
        {
            var separator = reader.Name.LastIndexOf('@');
            if (separator <= 0)
            {
                throw new InvalidDataException("Missing workbook sheet type: " + reader.Name);
            }

            var name = reader.Name[..separator];
            var type = reader.Name[(separator + 1)..];
            if (data.ContainsKey(name))
            {
                throw new InvalidDataException("Duplicate workbook sheet: " + name);
            }

            var rows = new List<JsonNode?[]>();
            while (reader.Read())
            {
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(column => Cell(reader.GetValue(column))).ToArray());
            }
            if (type == "vlist")
            {
                rows = Enumerable.Range(0, reader.FieldCount).Select(column => rows.Select(row => row[column]).ToArray()).ToList();
            }
            var meta = originalMetadata?[name]?.DeepClone() as JsonObject ?? new JsonObject();
            meta["name"] = name;
            meta["type"] = type;
            if (type is "list" or "vlist")
            {
                if (rows.Count == 0)
                {
                    throw new InvalidDataException("Missing workbook headers: " + name);
                }

                var keys = rows[0].Select(value => value?.ToString() ?? "").ToArray();
                var names = keys.Select(key => key.StartsWith('%') ? key[1..] : key).ToArray();
                meta["keys"] = new JsonArray(names.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray());
                meta["keyMeta"] = new JsonArray(keys.Select(key => key.Length == 0 ? null : key.StartsWith('%') ? (JsonNode?)JsonValue.Create("localizable") : JsonValue.Create(false)).ToArray());
                var values = new JsonArray();
                foreach (var row in rows.Skip(1))
                {
                    if (row.FirstOrDefault()?.ToString().StartsWith("//", StringComparison.Ordinal) == true)
                    {
                        continue;
                    }

                    var item = new JsonObject();
                    for (var column = 0; column < names.Length; column++)
                    {
                        if (names[column].Length > 0)
                        {
                            item[names[column]] = column < row.Length ? row[column]?.DeepClone() : null;
                        }
                    }
                    values.Add(item);
                }
                data[name] = values;
            }
            else if (type == "dict")
            {
                var values = new JsonObject();
                var comments = new JsonObject();
                var sequence = new JsonArray();
                foreach (var row in rows)
                {
                    var key = row.FirstOrDefault()?.ToString();
                    if (string.IsNullOrEmpty(key) || key.StartsWith("//", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (values.ContainsKey(key))
                    {
                        throw new InvalidDataException("Duplicate workbook key: " + key);
                    }

                    values[key] = row.Length > 1 ? row[1]?.DeepClone() : null;
                    comments[key] = row.Length > 2 ? row[2]?.DeepClone() : null;
                    sequence.Add(key);
                }
                meta["comment"] = comments;
                meta["sequence"] = sequence;
                data[name] = values;
            }
            else if (type == "raw")
            {
                data[name] = new JsonArray(rows.Select(row => (JsonNode)new JsonArray(row.Select(value => value?.DeepClone()).ToArray())).ToArray());
            }
            else
            {
                throw new NotSupportedException("Unsupported workbook sheet type: " + type);
            }
            metadata[name] = meta;
        } while (reader.NextResult());
        return new Result(data, metadata);
    }

    private static JsonNode? Cell(object? value)
    {
        return value switch
        {
            null => null,
            bool flag => JsonValue.Create(flag),
            double number when double.IsFinite(number) => JsonValue.Create(number),
            int number => JsonValue.Create(number),
            string text => JsonValue.Create(text),
            DateTime => throw new NotSupportedException("Date-formatted cells are not supported; use numbers or text"),
            _ => throw new InvalidDataException("Unsupported workbook cell value")
        };
    }
}
