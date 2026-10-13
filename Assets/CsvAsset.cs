using System.Text;
using System.Text.Json.Nodes;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class CsvAsset : JsonAsset
{
    private readonly string _name;
    public CsvAsset(JsonNode? data, string name) : base(data)
    {
        _name = name;
    }

    public string ToCsv()
    {
        var rows = new List<IEnumerable<JsonNode?>>();
        if (_name.EndsWith(".list.csv", StringComparison.Ordinal))
        {
            var items = Data!.AsArray();
            if (items.Count == 0)
            {
                return "\n";
            }

            var keys = items[0]!.AsObject().Select(pair => pair.Key).ToArray();
            rows.Add(keys.Select(key => JsonValue.Create(key)));
            rows.AddRange(items.Select(item => keys.Select(key => item![key])));
        }
        else if (_name.EndsWith(".dict.csv", StringComparison.Ordinal))
        {
            rows.AddRange(Data!.AsObject().Select(pair => new JsonNode?[] { JsonValue.Create(pair.Key), pair.Value }));
        }
        else
        {
            rows.AddRange(Data!.AsArray().Select(row => row!.AsArray().AsEnumerable()));
        }

        return string.Join("\n", rows.Select(row => string.Join(",", row.Select(Cell))));
    }

    public override Asset CompileSource(Stream stream)
    {
        return new CsvAsset(Compilation.CsvCompiler.Compile(Create<TextAsset>(stream).Text, _name), _name);
    }

    private static string Cell(JsonNode? node)
    {
        var text = node is JsonValue value && value.TryGetValue<string>(out var str) ? str : node?.ToJsonString() ?? "";
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    public override void SaveTo(string path)
    {
        path = SafePath.PrepareFile(path);
        File.WriteAllText(path, ToCsv(), new UTF8Encoding(false));
    }
}
