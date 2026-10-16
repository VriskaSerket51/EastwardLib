using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EastwardLib.Utils;

namespace EastwardLib.Compilation;

public static class LocaleCompiler
{
    public static JsonObject ReadPo(string source)
    {
        var result = new JsonObject();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? field = null;
        var fuzzy = false;
        var obsolete = false;
        var explicitEmpty = false;
        void Commit()
        {
            if (values.TryGetValue("msgid", out var id) && id.Length > 0 && !obsolete)
            {
                if (values.ContainsKey("msgctxt"))
                {
                    throw new NotSupportedException("Contextual PO entries are not supported by the locale table");
                }

                if (!seen.Add(id))
                {
                    throw new InvalidDataException("Duplicate PO message: " + id);
                }

                if (!fuzzy && values.TryGetValue("msgstr", out var text) && (text.Length > 0 || explicitEmpty))
                {
                    result[id] = text;
                }
            }
            values.Clear();
            field = null;
            fuzzy = false;
            obsolete = false;
            explicitEmpty = false;
        }
        foreach (var raw in source.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                Commit();
                continue;
            }
            if (line.StartsWith("#~", StringComparison.Ordinal))
            {
                obsolete = true;
                continue;
            }
            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#,", StringComparison.Ordinal) && line[2..].Split(',').Any(flag => flag.Trim() == "fuzzy"))
                {
                    fuzzy = true;
                }

                if (line.StartsWith("#,", StringComparison.Ordinal) && line[2..].Split(',').Any(flag => flag.Trim() == "eastward-empty"))
                {
                    explicitEmpty = true;
                }
                continue;
            }
            if (line.StartsWith("msgid_plural", StringComparison.Ordinal) || line.StartsWith("msgstr[", StringComparison.Ordinal))
            {
                throw new NotSupportedException("Plural PO messages require the engine's plural locale schema");
            }

            var match = Regex.Match(line, "^(msgid|msgstr|msgctxt)\\s+(\".*\")$");
            if (match.Success)
            {
                if (match.Groups[1].Value == "msgid" && values.ContainsKey("msgid"))
                {
                    Commit();
                }

                field = match.Groups[1].Value;
                if (!values.TryAdd(field, Decode(match.Groups[2].Value)))
                {
                    throw new InvalidDataException("Repeated PO field: " + field);
                }
            }
            else if (line.StartsWith('"') && field != null)
            {
                values[field] += Decode(line);
            }
            else
            {
                throw new InvalidDataException("Invalid PO line: " + line);
            }
        }
        Commit();
        return result;
    }

    private static string Decode(string value)
    {
        var parsed = LuaTable.Parse(value);
        return parsed is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : throw new InvalidDataException("Invalid PO string");
    }

    public static string WriteLua(JsonNode? value)
    {
        if (value == null)
        {
            return "nil";
        }

        if (value is JsonObject obj)
        {
            return "{\n" + string.Join("\n", obj.Select(pair => "[" + Quote(pair.Key) + "]=" + WriteLua(pair.Value) + ",")) + "\n}";
        }
        if (value is JsonArray array)
        {
            return "{" + string.Join(',', array.Select(WriteLua)) + "}";
        }

        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            return Quote(text);
        }

        return value.ToJsonString();
    }

    private static string Quote(string text)
    {
        var result = new StringBuilder("\"");
        foreach (var character in text)
        {
            result.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when character < 32 => "\\" + ((int)character).ToString("D3"),
                _ => character.ToString()
            });
        }
        return result.Append('"').ToString();
    }
}
