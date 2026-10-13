using System.Text;
using System.Text.Json.Nodes;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class GraphAsset : TextAsset
{
    public string GraphType
    {
        get;
    }

    public GraphAsset(string type, string source) : base(source)
    {
        GraphType = type;
    }

    public string GetGraphMl()
    {
        if (GraphType == "fsm_scheme")
        {
            return SourceGraph.FromFsm(Text);
        }

        if (string.IsNullOrWhiteSpace(Text))
        {
            throw new FileNotFoundException("No deployed graph topology is available");
        }

        var data = JsonNode.Parse(Text);
        if (data is not JsonObject)
        {
            throw new FileNotFoundException("No deployed graph topology is available");
        }

        return SourceGraph.Write(GraphType, data);
    }

    public override Asset CompileSource(Stream stream)
    {
        return new GraphAsset(GraphType, Compilation.GraphCompiler.Compile(GraphType, Create<TextAsset>(stream).Text, Text));
    }

    public void SaveSourceTo(string path)
    {
        path = SafePath.PrepareFile(path);
        File.WriteAllText(path, GetGraphMl(), new UTF8Encoding(false));
    }
}
