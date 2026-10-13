using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EastwardLib.Utils;

namespace EastwardLib.Compilation;

public static class SQCompiler
{
    private sealed record Node(JsonObject Data, int Indent, string Name, Node? Parent);
    private const string Identifier = "[A-Za-z_][A-Za-z0-9_.]*";

    public static JsonObject Compile(string source, string fileName = "<noname>")
    {
        var root = new Node(new JsonObject { ["type"] = "root", ["children"] = new JsonArray(), ["line"] = 0, ["lineCount"] = 0, ["file"] = fileName }, -1, "__root__", null);
        var context = root;
        Node? previous = null;
        var longNode = false;
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            var directives = new JsonArray();
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                foreach (Match match in Regex.Matches(line[comment..], @"\$(\w+)(?:\(([^()]*)\))?"))
                {
                    directives.Add(new JsonObject { ["name"] = match.Groups[1].Value, ["value"] = match.Groups[2].Value });
                }
                line = line[..comment];
            }
            var indent = line.TakeWhile(character => character == '\t').Count();
            if (longNode && previous != null)
            {
                if (line.Length > indent && line[indent] == ' ')
                {
                    throw Error(lineIndex, "Space in multiline indentation");
                }
                if (indent > previous.Indent)
                {
                    previous.Data["args"]!.AsArray().Add(line[indent..]);
                    previous.Data["lineCount"] = previous.Data["lineCount"]!.GetValue<int>() + 1;
                    AddDirectives(previous.Data, directives);
                    continue;
                }
                longNode = false;
            }
            int? statementIndent = null;
            foreach (var statement in line.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(statement))
                {
                    continue;
                }
                var currentIndent = statementIndent ?? statement.TakeWhile(character => character == '\t').Count();
                // Match Gii's indentation for semicolon-separated statements.
                if (statementIndent is null or 0)
                {
                    statementIndent = currentIndent;
                }
                var text = statement.Trim();
                if (previous != null && currentIndent != previous.Indent)
                {
                    if (currentIndent > context.Indent)
                    {
                        context = previous;
                    }
                    else
                    {
                        while (currentIndent <= context.Indent)
                        {
                            context = context.Parent ?? throw Error(lineIndex, "Invalid indentation");
                        }
                    }
                }
                var data = new JsonObject { ["children"] = new JsonArray(), ["line"] = lineIndex + 1, ["lineCount"] = 1 };
                var name = "";
                if (text.StartsWith('!'))
                {
                    RequireIdentifier(text[1..], lineIndex);
                    data["type"] = "label";
                    data["id"] = text[1..];
                }
                else if (text.StartsWith('@'))
                {
                    var names = Regex.Split(text[1..], @"[\s,]+").Where(value => value.Length > 0).ToArray();
                    if (names.Length == 0)
                    {
                        throw Error(lineIndex, "Empty context");
                    }

                    foreach (var value in names)
                    {
                        RequireIdentifier(value, lineIndex);
                    }

                    name = "__context__";
                    data["type"] = "context";
                    data["names"] = new JsonArray(names.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                }
                else if (text.StartsWith('#'))
                {
                    var tags = new JsonArray();
                    var remaining = text;
                    while (remaining.Length > 0)
                    {
                        var tag = Regex.Match(remaining, "^#(" + Identifier + @")(?:\s*\(([^)]*)\))?\s*");
                        if (!tag.Success)
                        {
                            throw Error(lineIndex, "Invalid tag");
                        }

                        tags.Add(new JsonArray(JsonValue.Create(tag.Groups[1].Value), tag.Groups[2].Success ? JsonValue.Create(tag.Groups[2].Value.Trim()) : null));
                        remaining = remaining[tag.Length..];
                    }
                    data["type"] = "tag";
                    data["tags"] = tags;
                }
                else
                {
                    var action = Regex.Match(text, "^([.$]?)((?:" + Identifier + @"))(.*)$");
                    if (!action.Success)
                    {
                        throw Error(lineIndex, "Invalid statement");
                    }

                    var prefix = action.Groups[1].Value;
                    name = action.Groups[2].Value;
                    var tail = action.Groups[3].Value;
                    if (tail.Length > 0 && !char.IsWhiteSpace(tail[0]) && tail[0] != ':')
                    {
                        throw Error(lineIndex, "Invalid argument separator");
                    }

                    tail = tail.TrimStart();
                    if (prefix == "$")
                    {
                        if (tail.Length > 0 && !tail.StartsWith(':'))
                        {
                            throw Error(lineIndex, "Invalid directive");
                        }

                        data["type"] = "directive";
                        data["name"] = name;
                        data["value"] = tail.Length == 0 ? null : JsonValue.Create(tail[1..]);
                    }
                    else
                    {
                        if (prefix == ".")
                        {
                            if (context == root)
                            {
                                throw Error(lineIndex, "Subnode at root");
                            }

                            name = context.Name + "." + name;
                        }
                        var control = name is "if" or "elseif" or "switch" or "case" or "else" or "default";
                        var sibling = context.Data["children"]!.AsArray().LastOrDefault()?["name"]?.ToString();
                        if (name is "elseif" or "else" && sibling is not ("if" or "elseif"))
                        {
                            throw Error(lineIndex, "Else without if");
                        }

                        if (name is "case" or "default" && context.Name != "switch")
                        {
                            throw Error(lineIndex, "Case outside switch");
                        }

                        if (name is "if" or "elseif" or "switch" or "case" && tail.Length == 0)
                        {
                            throw Error(lineIndex, "Missing condition");
                        }

                        if (name is "else" or "default" && tail.Length > 0)
                        {
                            throw Error(lineIndex, "Unexpected condition");
                        }

                        var args = new JsonArray();
                        longNode = !control && tail.StartsWith(':');
                        if (longNode)
                        {
                            if (tail.Length > 1)
                            {
                                args.Add(tail[1..]);
                            }
                        }
                        else if (tail.Length > 0)
                        {
                            foreach (var value in control ? [tail] : Regex.Split(tail, @"\s+"))
                            {
                                args.Add(value);
                            }
                        }
                        data["type"] = "action";
                        data["name"] = name;
                        data["args"] = args;
                        data["sub"] = false;
                    }
                }
                if (context.Name == "switch" && name is not ("case" or "default"))
                {
                    throw Error(lineIndex, "Switch only accepts case or default");
                }

                AddDirectives(data, directives);
                context.Data["children"]!.AsArray().Add(data);
                previous = new Node(data, currentIndent, name, context);
            }
        }
        return root.Data;
    }

    private static void AddDirectives(JsonObject node, JsonArray directives)
    {
        if (directives.Count == 0)
        {
            return;
        }

        node["inlineDirectives"] ??= new JsonArray();
        foreach (var directive in directives)
        {
            node["inlineDirectives"]!.AsArray().Add(directive!.DeepClone());
        }
    }

    private static void RequireIdentifier(string value, int line)
    {
        if (!Regex.IsMatch(value, "^" + Identifier + "$"))
        {
            throw Error(line, "Invalid identifier");
        }
    }

    private static InvalidDataException Error(int line, string message)
    {
        return new InvalidDataException($"SQ line {line + 1}: {message}");
    }
}
