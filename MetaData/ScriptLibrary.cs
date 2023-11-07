using SimpleJSON;

namespace EastwardLib.MetaData;

public class ScriptLibrary : Dictionary<string, string>
{
    private ScriptLibrary(int capacity = 100) : base(capacity)
    {
    }


    public static ScriptLibrary Create(string json)
    {
        var root = JSONNode.Parse(json);
        ScriptLibrary scriptLibrary = new ScriptLibrary(root.Count);

        var exportNode = root["export"];
        var sourceNode = root["source"];

        foreach (var (key, value) in sourceNode)
        {
            string export = exportNode[key].Value;
            scriptLibrary[value.Value] = export;
        }

        return scriptLibrary;
    }
}