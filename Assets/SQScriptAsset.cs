using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class SQScriptAsset : JsonAsset
{
    public SQScriptAsset(JsonNode data) : base(data) { }

    public override Asset CompileSource(string path)
    {
        return new SQScriptAsset(Compilation.SQCompiler.Compile(File.ReadAllText(path), Data?["file"]?.ToString() ?? Path.GetFileName(path)));
    }

    public override Asset CompileSource(Stream stream)
    {
        return new SQScriptAsset(Compilation.SQCompiler.Compile(Create<TextAsset>(stream).Text, Data?["file"]?.ToString() ?? "script.sq"));
    }

    public string GetSource()
    {
        var output = new StringBuilder();
        foreach (var child in Data?["children"]?.AsArray() ?? [])
        {
            AppendNode(output, child!, 0);
        }

        return output.ToString();
    }

    private static void AppendNode(StringBuilder output, JsonNode node, int depth)
    {
        var indent = new string('\t', depth);
        var line = new StringBuilder();
        var type = node["type"]?.ToString();
        switch (type)
        {
            case "label":
                line.Append(indent).Append('!').Append(node["id"]);
                break;
            case "context":
                line.Append(indent).Append('@').AppendJoin(',', node["names"]!.AsArray().Select(value => value!.ToString()));
                break;
            case "tag":
                line.Append(indent);
                foreach (var tag in node["tags"]!.AsArray())
                {
                    line.Append('#').Append(tag![0]);
                    if (!string.IsNullOrEmpty(tag[1]?.ToString()))
                    {
                        line.Append('(').Append(tag[1]).Append(')');
                    }

                    line.Append(' ');
                }
                break;
            case "directive":
                line.Append(indent).Append('$').Append(node["name"]);
                if (node["value"] != null)
                {
                    line.Append(':').Append(node["value"]);
                }

                break;
            case "action":
                var name = node["name"]!.ToString();
                var args = (node["args"]?.AsArray() ?? []).Select(arg => arg?.ToString() ?? "").ToArray();
                var block = !new[] { "if", "elseif", "else", "switch", "case", "default" }.Contains(name)
                    && ((node["lineCount"]?.GetValue<int>() ?? 0) > 1 || args.Any(arg => Regex.IsMatch(arg, @"\s")));
                line.Append(indent).Append(name);
                if (block)
                {
                    line.Append(':').Append(args.FirstOrDefault()).Append('\n');
                    if (args.FirstOrDefault() == "")
                    {
                        line.Append(indent).Append("\t//\n");
                    }

                    foreach (var arg in args.Skip(1))
                    {
                        line.Append(indent).Append('\t').Append(arg).Append(string.IsNullOrWhiteSpace(arg) ? "//" : "").Append('\n');
                    }
                }
                else if (args.Length > 0)
                {
                    line.Append(' ').AppendJoin(' ', args);
                }

                break;
        }
        if (line.Length > 0)
        {
            var text = line.ToString();
            if (text.EndsWith('\n'))
            {
                text = text[..^1];
            }
            if (type == "tag")
            {
                text = text.TrimEnd();
            }
            if (node["inlineDirectives"] is JsonArray directives && directives.Count > 0)
            {
                text += "//";
                foreach (var directive in directives)
                {
                    text += "$" + directive!["name"] + "(" + (Truthy(directive["value"]) ? directive["value"] : "") + ") ";
                }
                text = text.TrimEnd();
            }
            output.Append(text).Append('\n');
        }
        foreach (var child in node["children"]?.AsArray() ?? [])
        {
            AppendNode(output, child!, depth + 1);
        }
    }

    private static bool Truthy(JsonNode? node)
    {
        if (node == null)
        {
            return false;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag))
            {
                return flag;
            }

            if (value.TryGetValue<double>(out var number))
            {
                return number != 0;
            }

            if (value.TryGetValue<string>(out var text))
            {
                return text.Length > 0;
            }
        }
        return true;
    }

    public override void SaveTo(string path)
    {
        path = SafePath.PrepareFile(path);
        File.WriteAllText(path, GetSource(), new UTF8Encoding(false));
    }
}
