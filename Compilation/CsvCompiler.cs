using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using EastwardLib.Utils;

namespace EastwardLib.Compilation;

public static class CsvCompiler
{
    public static JsonNode Compile(string source, string name)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var closed = false;
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < source.Length && source[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                        closed = true;
                    }
                }
                else
                {
                    cell.Append(character);
                }
            }
            else if (character == '"' && cell.Length == 0 && !closed)
            {
                quoted = true;
            }
            else if (character is ',' or '\n' or '\r')
            {
                row.Add(cell.ToString());
                cell.Clear();
                closed = false;
                if (character != ',')
                {
                    rows.Add(row);
                    row = [];
                    if (character == '\r' && index + 1 < source.Length && source[index + 1] == '\n')
                    {
                        index++;
                    }
                }
            }
            else if (closed || character == '"')
            {
                throw new InvalidDataException("Invalid CSV quote");
            }
            else
            {
                cell.Append(character);
            }
        }
        if (quoted)
        {
            throw new InvalidDataException("Unterminated CSV quote");
        }

        if (cell.Length > 0 || row.Count > 0 || closed)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        static JsonNode? Value(string text) => text.Length == 0 ? null : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? JsonValue.Create(number) : JsonValue.Create(text);
        if (name.EndsWith(".dict.csv", StringComparison.Ordinal))
        {
            var result = new JsonObject();
            foreach (var item in rows)
            {
                if (item.Count < 2)
                {
                    throw new InvalidDataException("CSV dictionary requires two columns");
                }

                if (item[0].Length > 0)
                {
                    result.Add(item[0], Value(item[1]));
                }
            }
            return result;
        }
        if (name.EndsWith(".list.csv", StringComparison.Ordinal))
        {
            var result = new JsonArray();
            if (rows.Count == 0)
            {
                return result;
            }

            foreach (var item in rows.Skip(1))
            {
                if (item.Count != rows[0].Count)
                {
                    throw new InvalidDataException("CSV row and header widths differ");
                }

                var values = new JsonObject();
                for (var index = 0; index < item.Count; index++)
                {
                    values.Add(rows[0][index], Value(item[index]));
                }

                result.Add(values);
            }
            return result;
        }
        return new JsonArray(rows.Select(item => (JsonNode)new JsonArray(item.Select(Value).ToArray())).ToArray());
    }
}
