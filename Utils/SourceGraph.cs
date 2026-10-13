using System.Text.Json.Nodes;
using System.Text;
using System.Xml.Linq;

namespace EastwardLib.Utils;

public static class SourceGraph
{
    private sealed class Node(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public List<Node>? Children
        {
            get; set;
        }
        public string? Description
        {
            get; set;
        }
        public JsonNode? Transition
        {
            get; set;
        }
    }

    private sealed record Edge(string From, string To, string? Label = null, bool Dashed = false, string Color = "#000000");

    public static string Write(string type, JsonNode data)
    {
        var nodes = new List<Node>();
        var edges = new List<Edge>();
        static string? Label(JsonNode? value) => value == null || value.ToString() == "" || value is JsonValue scalar && scalar.TryGetValue<bool>(out var enabled) && !enabled ? null : value.ToString();
        if (type is "fsm_scheme" or "quest_scheme")
        {
            Node Convert(JsonNode value) => new(value["id"]!.ToString(), value["name"]!.ToString())
            {
                Children = value["children"] is JsonArray children && children.Count > 0 ? children.Select(child => Convert(child!)).ToList() : null,
                Description = value["description"]?.ToString(),
                Transition = value["transition"] is JsonValue transition && transition.TryGetValue<bool>(out var enabled) && !enabled ? null : value["transition"]
            };
            nodes.AddRange(data["nodes"]!.AsArray().Select(value => Convert(value!)));
            if (type == "quest_scheme")
            {
                edges.AddRange(data["connections"]!.AsArray().Select(value => new Edge(value!["from"]!.ToString(), value["to"]!.ToString(), Label(value["cond"]), value["type"]?.ToString() == "spawn")));
            }
            else
            {
                edges.AddRange(data["edges"]!.AsArray().Select(value => new Edge(value!["from"]!.ToString(), value["to"]!.ToString(), value["label"]?.ToString())));
            }
        }
        else if (type == "story_graph")
        {
            Node Convert(JsonNode value)
            {
                var id = value["id"]!.ToString();
                if (value["fullId"]?.ToString() != id)
                {
                    throw new InvalidDataException("Story node ID differs from its full ID");
                }

                var group = value["type"]?.ToString() is "GROUP" or "REF";
                return new Node(id, value["text"]?.ToString() ?? "")
                {
                    Children = value["children"] is JsonArray children && children.Count > 0 ? children.Select(child => Convert(child!)).ToList() : group ? [] : null,
                    Description = Label(value["type"]) is string kind ? "type=" + kind : null
                };
            }
            nodes.AddRange(data["rootNodes"]!.AsArray().Select(value => Convert(value!)));
            foreach (var value in data["edges"]!.AsArray())
            {
                var kind = value!["type"]!.ToString();
                if (kind is not ("NORMAL" or "NOT" or "COMMENT"))
                {
                    throw new InvalidDataException("Unsupported story edge type: " + kind);
                }

                edges.Add(new Edge(value["src"]!.ToString(), value["dst"]!.ToString(), value["text"]?.ToString(), kind == "COMMENT", kind == "NOT" ? "#FF0000" : "#000000"));
            }
        }
        else if (type == "scene_portal_graph")
        {
            var groups = new Dictionary<string, Node>(StringComparer.Ordinal);
            Node Group(string name)
            {
                if (groups.TryGetValue(name, out var existing))
                {
                    return existing;
                }

                var index = name.LastIndexOf('.');
                var parent = index < 0 ? null : Group(name[..index]);
                var group = new Node("group_" + groups.Count, index < 0 ? name : name[(index + 1)..]) { Children = [] };
                groups.Add(name, group);
                (parent?.Children ?? nodes).Add(group);
                return group;
            }
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            var portalIndex = 0;
            foreach (var value in data["nodes"]!.AsArray())
            {
                var full = value!["fullname"]!.ToString();
                var name = value["name"]!.ToString();
                var id = "portal_" + portalIndex++;
                ids[full] = id;
                var prefix = full[..^name.Length].TrimEnd('.');
                (prefix.Length == 0 ? nodes : Group(prefix).Children!).Add(new Node(id, name));
            }
            foreach (var (name, value) in data["group_meta"] as JsonObject ?? [])
            {
                Group(name).Children!.Add(new Node("meta_" + groups.Count + "_" + name, "@" + value));
            }

            edges.AddRange(data["connections"]!.AsArray().Select(value => new Edge(ids[value!["a"]!.ToString()], ids[value["b"]!.ToString()], Label(value["data"]))));
        }
        else
        {
            throw new NotSupportedException("Unsupported editable graph: " + type);
        }

        return Render(nodes, edges);
    }

    public static string FromFsm(string script)
    {
        var states = FsmSource.Parse(script);
        var nodes = states.ToDictionary(pair => pair.Key, pair => new Node(pair.Key, pair.Value!["localName"]!.ToString())
        {
            Children = pair.Value!["type"]?.ToString() == "group" ? [] : null
        }, StringComparer.Ordinal);
        var roots = new List<Node>();
        var edges = new List<Edge>();
        string Parent(string name) => name[..^states[name]!["localName"]!.ToString().Length].TrimEnd('.');
        static string Destination(JsonNode? value) => value is JsonObject table
            ? table.Where(pair => int.TryParse(pair.Key, out _)).MaxBy(pair => int.Parse(pair.Key)).Value!.ToString()
            : value?.ToString() ?? throw new InvalidDataException("Missing FSM transition destination");
        foreach (var (name, state) in states)
        {
            var parent = Parent(name);
            if (parent.Length > 0 && (!nodes.TryGetValue(parent, out var group) || group.Children == null))
            {
                throw new InvalidDataException("Missing FSM parent: " + parent);
            }

            (parent.Length == 0 ? roots : nodes[parent].Children!).Add(nodes[name]);
            foreach (var (label, value) in state!["jump"] as JsonObject ?? [])
            {
                var target = Destination(value);
                if (!nodes.ContainsKey(target))
                {
                    throw new InvalidDataException("Missing FSM jump target: " + target);
                }

                edges.Add(new Edge(name, target, label));
            }
            var next = state["next"] as JsonObject;
            foreach (var (label, value) in next ?? [])
            {
                if (label is "true" or "True" && next!.Any(pair => pair.Key is not ("true" or "True") && Destination(pair.Value) == Destination(value)))
                {
                    continue;
                }

                var target = Destination(value);
                if (label.StartsWith('$') && label[1..] != states[target]?["localName"]?.ToString())
                {
                    target = Parent(target);
                }

                if (!nodes.ContainsKey(target))
                {
                    throw new InvalidDataException("Missing FSM next target: " + target);
                }

                edges.Add(new Edge(name, target));
            }
        }
        var additions = new Dictionary<string, Node>(StringComparer.Ordinal);
        for (var i = edges.Count - 1; i >= 0; i--)
        {
            var edge = edges[i];
            if (string.IsNullOrEmpty(edge.Label) || nodes[edge.From].Children == null)
            {
                continue;
            }

            if (states[edge.To]?["jump"] is JsonObject jump && jump.ContainsKey(edge.Label))
            {
                continue;
            }

            var scope = Parent(edge.From);
            while (true)
            {
                if (states.Where(pair => scope.Length == 0 || pair.Key.StartsWith(scope + ".", StringComparison.Ordinal)).All(pair => pair.Key == edge.To || pair.Value!["jump"] is JsonObject table && table.ContainsKey(edge.Label)))
                {
                    var id = (scope.Length == 0 ? "" : scope + ".") + "ANY";
                    if (!additions.ContainsKey(id))
                    {
                        var any = new Node(id, "ANY");
                        additions.Add(id, any);
                        (scope.Length == 0 ? roots : nodes[scope].Children!).Add(any);
                    }
                    edges[i] = edge with
                    {
                        From = id
                    };
                    break;
                }
                if (scope.Length == 0)
                {
                    break;
                }

                scope = Parent(scope);
            }
        }
        var anyEdges = edges.Where(edge => additions.ContainsKey(edge.From)).ToArray();
        edges = edges.Where(edge => additions.ContainsKey(edge.From) || !anyEdges.Any(any =>
        {
            var scope = any.From[..^3].TrimEnd('.');
            return edge.From != any.To && (scope.Length == 0 || edge.From.StartsWith(scope + ".", StringComparison.Ordinal)) && edge.Label == any.Label && edge.To == any.To;
        })).Distinct().ToList();
        return Render(roots, edges);
    }

    private static string Render(List<Node> nodes, List<Edge> edges)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Validate(IEnumerable<Node> items)
        {
            foreach (var node in items)
            {
                if (!ids.Add(node.Id))
                {
                    throw new InvalidDataException("Duplicate graph node: " + node.Id);
                }

                if (node.Children != null)
                {
                    Validate(node.Children);
                }
            }
        }
        Validate(nodes);
        if (edges.Any(edge => !ids.Contains(edge.From) || !ids.Contains(edge.To)))
        {
            throw new InvalidDataException("Missing graph edge endpoint");
        }

        XNamespace graphNs = "http://graphml.graphdrawing.org/xmlns";
        XNamespace y = "http://www.yworks.com/xml/graphml";
        var index = 0;
        XElement RenderNode(Node node)
        {
            var n = index++;
            var graphics = new XElement(y + (node.Children == null ? "ShapeNode" : "GroupNode"),
                new XElement(y + "Geometry", new XAttribute("x", n % 8 * 180), new XAttribute("y", n / 8 * 100), new XAttribute("width", 120), new XAttribute("height", 60)),
                node.Name.Length == 0 ? null : new XElement(y + "NodeLabel", new XAttribute("x", 0), new XAttribute("y", 0), new XAttribute("width", 120), new XAttribute("height", 30), new XAttribute("fontSize", 12), node.Name),
                new XElement(y + "BorderStyle", new XAttribute("color", "#000000"), new XAttribute("type", "line"), new XAttribute("width", 1)));
            return new XElement(graphNs + "node", new XAttribute("id", node.Id), node.Children == null ? null : new XAttribute("yfiles.foldertype", "group"),
                new XElement(graphNs + "data", new XAttribute("key", "d0"), "\n", graphics, "\n"),
                node.Description == null ? null : new XElement(graphNs + "data", new XAttribute("key", "d3"), node.Description),
                node.Transition == null ? null : new XElement(graphNs + "data", new XAttribute("key", "d2"), node.Transition.ToJsonString()),
                node.Children == null ? null : new XElement(graphNs + "graph", new XAttribute("id", node.Id + "_graph"), new XAttribute("edgedefault", "directed"), node.Children.Select(RenderNode)));
        }
        var root = new XElement(graphNs + "graphml", new XAttribute(XNamespace.Xmlns + "y", y.NamespaceName),
            new XElement(graphNs + "key", new XAttribute("id", "d0"), new XAttribute("for", "node"), new XAttribute("yfiles.type", "nodegraphics")),
            new XElement(graphNs + "key", new XAttribute("id", "d1"), new XAttribute("for", "edge"), new XAttribute("yfiles.type", "edgegraphics")));
        foreach (var (id, kind, name) in new[] { ("d2", "node", "transition"), ("d3", "node", "description"), ("d4", "edge", "color"), ("d5", "edge", "style") })
        {
            root.Add(new XElement(graphNs + "key", new XAttribute("id", id), new XAttribute("for", kind), new XAttribute("attr.name", name), new XAttribute("attr.type", "string")));
        }

        XElement RenderEdge(Edge edge, int i)
        {
            var graphics = new XElement(y + "PolyLineEdge",
                new XElement(y + "LineStyle", new XAttribute("color", edge.Color), new XAttribute("type", edge.Dashed ? "dashed" : "line"), new XAttribute("width", 1)));
            if (!string.IsNullOrEmpty(edge.Label))
            {
                graphics.Add(new XElement(y + "EdgeLabel", new XAttribute("x", 0), new XAttribute("y", 0), new XAttribute("width", 120), new XAttribute("height", 30), new XAttribute("fontSize", 12), edge.Label));
            }

            return new XElement(graphNs + "edge", new XAttribute("id", "e" + i), new XAttribute("source", edge.From), new XAttribute("target", edge.To),
                new XElement(graphNs + "data", new XAttribute("key", "d4"), edge.Color),
                new XElement(graphNs + "data", new XAttribute("key", "d5"), edge.Dashed ? "dashed" : "line"),
                new XElement(graphNs + "data", new XAttribute("key", "d1"), "\n", graphics, "\n"));
        }
        root.Add(new XElement(graphNs + "graph", new XAttribute("id", "G"), new XAttribute("edgedefault", "directed"), nodes.Select(RenderNode), edges.Select(RenderEdge)));
        // Some public Gii readers open XML using the OS code page. Numeric character
        // references retain Unicode labels even through that legacy text reader.
        var xml = root.ToString(SaveOptions.DisableFormatting);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + string.Concat(xml.EnumerateRunes().Select(rune => rune.Value > 127 ? "&#x" + rune.Value.ToString("X") + ";" : rune.ToString()));
    }
}
