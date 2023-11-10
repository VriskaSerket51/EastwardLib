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
        var rootKey = root["root"].Value;
        var map = root["map"];
        var groups = map[rootKey]["body"]["groups"].AsArray;
        foreach (var (_, value) in groups)
        {
            var childNode = map[value.Value]["body"];
            if (!childNode["atlasCachePath"].IsBoolean)
            {
                var atlasCachePath = childNode["atlasCachePath"].Value;
                var texturesNode = childNode["textures"].AsArray;
                foreach (var (_, textureNode) in texturesNode)
                {
                    var childTextureNode = map[textureNode.Value]["body"];
                    textureIndex.Add(childTextureNode["path"].Value, atlasCachePath);
                }
            }
        }

        return textureIndex;
    }
}