using SimpleJSON;

namespace EastwardLib.MetaData;

public class TextureIndex : Dictionary<string, string>
{

    private TextureIndex(int capacity = 100) : base(capacity)
    {
    }
    
    public static TextureIndex Create(string json)
    {
        var root = JSONNode.Parse(json);
        TextureIndex textureIndex = new TextureIndex(root.Count);

        return textureIndex;
    }
}