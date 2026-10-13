using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EastwardLib.Utils;

namespace EastwardLib.Compilation;

public static class GraphCompiler
{
    private sealed class Node(string id, string name, Node? parent, bool group, XElement source)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public Node? Parent { get; } = parent;
        public bool Group { get; } = group;
        public XElement Source { get; } = source;
        public string FullName => Parent == null ? Name : Parent.FullName + "." + Name;
        public List<Node> Children { get; } = [];
        public Dictionary<string, Node> Jump { get; } = new(StringComparer.Ordinal);
        public Dictionary<Node, bool> Next { get; } = [];
    }
    private sealed record Edge(Node From, Node To, string Label, string Style, string Color);
    private static readonly XNamespace G = "http://graphml.graphdrawing.org/xmlns";
    private static readonly XNamespace Y = "http://www.yworks.com/xml/graphml";

    public static string Compile(string type, string source, string? original = null)
    {
        var document = XDocument.Parse(source);
        var root = document.Root ?? throw new InvalidDataException("Empty GraphML");
        var keys = root.Elements(G + "key").Where(key => key.Attribute("attr.name") != null).ToDictionary(key => (string)key.Attribute("id")!, key => (string)key.Attribute("attr.name")!, StringComparer.Ordinal);
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        var roots = new List<Node>();
        string Attribute(Node node, string key)
        {
            return node.Source.Elements(G + "data").FirstOrDefault(data => keys.GetValueOrDefault((string?)data.Attribute("key") ?? "") == key)?.Value ?? "";
        }
        void ReadNodes(XElement graph, Node? parent)
        {
            foreach (var element in graph.Elements(G + "node"))
            {
                var name = element.Elements(G + "data").SelectMany(data => data.Descendants(Y + "NodeLabel")).FirstOrDefault()?.Value ?? "";
                var node = new Node((string?)element.Attribute("id") ?? throw new InvalidDataException("Missing graph ID"), name, parent, element.Element(G + "graph") != null, element);
                if (!nodes.TryAdd(node.Id, node))
                {
                    throw new InvalidDataException("Duplicate graph ID: " + node.Id);
                }

                (parent?.Children ?? roots).Add(node);
                if (node.Group)
                {
                    ReadNodes(element.Element(G + "graph")!, node);
                }
            }
        }
        ReadNodes(root.Element(G + "graph") ?? throw new InvalidDataException("Missing graph"), null);
        var edges = root.Descendants(G + "edge").Select(element =>
        {
            var from = (string?)element.Attribute("source") ?? "";
            var to = (string?)element.Attribute("target") ?? "";
            if (!nodes.ContainsKey(from) || !nodes.ContainsKey(to))
            {
                throw new InvalidDataException("Missing graph edge endpoint");
            }

            var label = element.Descendants(Y + "EdgeLabel").FirstOrDefault()?.Value ?? "";
            var style = (string?)element.Descendants(Y + "LineStyle").FirstOrDefault()?.Attribute("type") ?? "line";
            var color = (string?)element.Descendants(Y + "LineStyle").FirstOrDefault()?.Attribute("color") ?? "#000000";
            return new Edge(nodes[from], nodes[to], label, style, color);
        }).ToArray();
        if (type == "fsm_scheme")
        {
            return Fsm(nodes.Values, edges, original);
        }

        var data = original == null ? new JsonObject() : JsonNode.Parse(original)?.AsObject().DeepClone().AsObject() ?? new JsonObject();
        if (type == "scene_portal_graph")
        {
            var portals = nodes.Values.Where(node => !node.Group && node.Name.Length > 0 && !node.Name.StartsWith('@')).ToArray();
            var names = portals.ToHashSet();
            var metadata = new JsonObject();
            foreach (var node in nodes.Values.Where(node => node.Name.StartsWith('@')))
            {
                if (node.Parent == null)
                {
                    throw new InvalidDataException("Portal metadata outside a group");
                }

                metadata.Add(node.Parent.FullName, node.Name[1..]);
            }
            data["nodes"] = new JsonArray(portals.Select(node => (JsonNode)new JsonObject { ["name"] = node.Name, ["fullname"] = node.FullName }).ToArray());
            data["connections"] = new JsonArray(edges.Select(edge =>
            {
                if (!names.Contains(edge.From) || !names.Contains(edge.To))
                {
                    throw new InvalidDataException("Portal edge must connect portal nodes");
                }

                return (JsonNode)new JsonObject { ["a"] = edge.From.FullName, ["b"] = edge.To.FullName, ["data"] = edge.Label.Length == 0 ? JsonValue.Create(false) : JsonValue.Create(edge.Label) };
            }).ToArray());
            data["group_meta"] = metadata;
        }
        else if (type is "quest_scheme" or "story_graph")
        {
            var originals = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            void Collect(JsonArray? array)
            {
                foreach (var value in array ?? [])
                {
                    originals[value!["id"]!.ToString()] = value;
                    Collect(value["children"] as JsonArray);
                }
            }
            Collect(data[type == "quest_scheme" ? "nodes" : "rootNodes"] as JsonArray);
            var included = new HashSet<Node>();
            JsonNode? Convert(Node node)
            {
                if (type == "quest_scheme" && (node.Name.StartsWith("//", StringComparison.Ordinal) || node.Source.Elements(G + "data").SelectMany(element => element.Descendants(Y + "BorderStyle")).Any(element => (string?)element.Attribute("type") == "dotted")))
                {
                    return null;
                }

                var value = originals.GetValueOrDefault(node.Id)?.DeepClone().AsObject() ?? new JsonObject();
                value["id"] = node.Id;
                var children = new JsonArray(node.Children.Select(Convert).Where(child => child != null).ToArray());
                included.Add(node);
                if (type == "quest_scheme")
                {
                    value["name"] = node.Name;
                    value["fullname"] = node.FullName;
                    value["type"] = node.Name switch
                    {
                        "AND" => "and",
                        "ABORT" => "abort",
                        "RESET" => "reset",
                        "RESUME" => "resume",
                        "PAUSE" => "pause",
                        _ => "node"
                    };
                    value["children"] = children;
                    var transition = Attribute(node, "transition");
                    value["transition"] = transition.Length > 0 ? JsonNode.Parse(transition) : JsonValue.Create(false);
                }
                else
                {
                    var description = Regex.Match(Attribute(node, "description"), @"type\s*=\s*(\w+)");
                    var kind = description.Success ? description.Groups[1].Value : node.Group ? "GROUP" : "";
                    if (children.Count > 0 && kind is not ("GROUP" or "REF"))
                    {
                        throw new InvalidDataException("Story children require GROUP or REF type");
                    }

                    value["fullId"] = node.Id;
                    value["text"] = node.Name;
                    value["type"] = kind.Length > 0 ? JsonValue.Create(kind) : JsonValue.Create(false);
                    value["group"] = node.Parent == null ? JsonValue.Create(false) : JsonValue.Create(node.Parent.Id);
                    value["visual"] ??= new JsonObject();
                    if (kind is "GROUP" or "REF")
                    {
                        value["children"] = children;
                    }
                    else
                    {
                        value.Remove("children");
                    }
                }
                return value;
            }
            data[type == "quest_scheme" ? "nodes" : "rootNodes"] = new JsonArray(roots.Select(Convert).Where(value => value != null).ToArray());
            var connections = new JsonArray();
            foreach (var edge in edges)
            {
                if (!included.Contains(edge.From) || !included.Contains(edge.To) || type == "quest_scheme" && edge.Label.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (type == "quest_scheme")
                {
                    var spawn = edge.Style == "dashed";
                    var fromType = edge.From.Name is "AND" or "ABORT" or "RESET" or "RESUME" or "PAUSE";
                    var toType = edge.To.Name is "AND" or "ABORT" or "RESET" or "RESUME" or "PAUSE";
                    if (fromType && (toType || spawn))
                    {
                        throw new InvalidDataException("Invalid quest command connection");
                    }

                    connections.Add(new JsonObject { ["from"] = edge.From.Id, ["to"] = edge.To.Id, ["cond"] = edge.Label.Length == 0 ? JsonValue.Create(false) : JsonValue.Create(edge.Label), ["type"] = spawn ? "spawn" : "move" });
                }
                else
                {
                    connections.Add(new JsonObject { ["src"] = edge.From.Id, ["dst"] = edge.To.Id, ["text"] = edge.Label, ["type"] = edge.Color.Equals("#FF0000", StringComparison.OrdinalIgnoreCase) ? "NOT" : edge.Style == "dashed" ? "COMMENT" : "NORMAL", ["visual"] = new JsonObject() });
                }
            }
            data[type == "quest_scheme" ? "connections" : "edges"] = connections;
        }
        else
        {
            throw new NotSupportedException("Unsupported graph type: " + type);
        }

        return data.ToJsonString();
    }

    private static string Fsm(IEnumerable<Node> values, IReadOnlyList<Edge> edges, string? original)
    {
        var nodes = values.ToArray();
        if (nodes.Any(node => node.Name.Length == 0) || nodes.Select(node => node.FullName).Distinct(StringComparer.Ordinal).Count() != nodes.Length)
        {
            throw new InvalidDataException("Invalid or duplicate FSM state name");
        }

        foreach (var edge in edges)
        {
            var target = edge.To.Group ? edge.To.Children.SingleOrDefault(node => node.Name == "start") ?? throw new InvalidDataException("Missing FSM group start") : edge.To;
            if (target.Name == "ANY")
            {
                throw new InvalidDataException("Cannot enter FSM ANY state");
            }

            if (edge.Label.Trim().Length > 0)
            {
                edge.From.Jump.Add(edge.Label.Trim(), target);
            }
            else
            {
                if (edge.From.Name == "ANY")
                {
                    throw new InvalidDataException("FSM ANY requires a condition");
                }

                var from = edge.From.Group ? edge.From.Children.SingleOrDefault(node => node.Name == "stop") ?? throw new InvalidDataException("Missing FSM group stop") : edge.From;
                from.Next.Add(target, edge.To.Group);
            }
        }
        foreach (var node in nodes)
        {
            for (var parent = node.Parent; parent != null; parent = parent.Parent)
            {
                foreach (var (label, target) in parent.Jump)
                {
                    node.Jump[label] = target;
                }
            }
        }
        foreach (var any in nodes.Where(node => node.Name == "ANY"))
        {
            foreach (var (label, target) in any.Jump)
            {
                foreach (var node in nodes.Where(node => node.Name != "ANY" && node != target && (any.Parent == null || node.FullName.StartsWith(any.Parent.FullName + ".", StringComparison.Ordinal))))
                {
                    node.Jump.TryAdd(label, target);
                }
            }
        }

        static string Function(Node node) => "_FSM_" + node.FullName.Replace('.', '_');
        static JsonNode Target(Node from, Node to)
        {
            if (from.Parent == to.Parent)
            {
                return JsonValue.Create(to.FullName)!;
            }

            var ancestors = new HashSet<Node>();
            for (var parent = from.Parent; parent != null; parent = parent.Parent)
            {
                ancestors.Add(parent);
            }

            Node? common = to.Parent;
            while (common != null && !ancestors.Contains(common))
            {
                common = common.Parent;
            }

            var result = new JsonArray();
            for (var parent = from.Parent; parent != common; parent = parent!.Parent)
            {
                result.Add(Function(parent!) + "__jumpout");
            }

            var enters = new Stack<Node>();
            for (var parent = to.Parent; parent != common; parent = parent!.Parent)
            {
                enters.Push(parent!);
            }

            foreach (var parent in enters)
            {
                result.Add(Function(parent) + "__jumpin");
            }

            result.Add(to.FullName);
            return result;
        }
        var old = original == null ? new JsonObject() : FsmSource.Parse(original);
        var states = new JsonObject();
        foreach (var node in nodes.Where(node => node.Name != "ANY"))
        {
            var state = old[node.FullName]?.DeepClone().AsObject() ?? new JsonObject();
            state["name"] = node.FullName;
            state["localName"] = node.Name;
            state["id"] = Function(node);
            state["type"] = node.Group ? "group" : "state";
            state.Remove("jump");
            state.Remove("next");
            states[node.FullName] = state;
        }
        var output = new StringBuilder("return (function()\nnodelist=").Append(LocaleCompiler.WriteLua(states)).Append('\n');
        foreach (var node in nodes.Where(node => node.Name != "ANY"))
        {
            var entry = "nodelist[" + LocaleCompiler.WriteLua(JsonValue.Create(node.FullName)) + "]";
            var jump = new JsonObject(node.Jump.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Target(node, pair.Value))));
            output.Append(entry).Append(".jump=").Append(jump.Count == 0 ? "false" : LocaleCompiler.WriteLua(jump)).Append('\n');
            if (node.Next.Count == 0)
            {
                continue;
            }

            output.Append(entry).Append(".next={\n");
            foreach (var (target, group) in node.Next)
            {
                output.Append('[').Append(LocaleCompiler.WriteLua(JsonValue.Create("$" + (group ? target.Parent!.Name : target.Name)))).Append("]=").Append(LocaleCompiler.WriteLua(Target(node, target))).Append(",\n");
            }
            if (node.Next.Count == 1)
            {
                output.Append("[true]=").Append(LocaleCompiler.WriteLua(Target(node, node.Next.Keys.First()))).Append(",\n");
            }

            output.Append("}\n");
        }
        return output.Append("return nodelist\nend) ()\n").ToString();
    }
}
