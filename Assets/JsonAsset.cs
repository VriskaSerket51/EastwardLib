using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EastwardLib.Utils;
using MessagePack;

namespace EastwardLib.Assets;

public class JsonAsset : Asset
{
    public JsonNode? Data
    {
        get; protected set;
    }

    private readonly bool _packed;

    public JsonAsset()
    {
    }

    public JsonAsset(JsonNode? data, bool packed = false)
    {
        Data = data?.DeepClone();
        _packed = packed;
    }

    public override byte[] Encode()
    {
        return _packed
        ? MessagePackSerializer.ConvertFromJson(Data?.ToJsonString() ?? "null")
        : Encoding.UTF8.GetBytes(Data?.ToJsonString() ?? "null");
    }

    public override void Decode(Stream stream)
    {
        Data = JsonNode.Parse(stream);
    }

    public static JsonAsset FromPacked(byte[] bytes)
    {
        return new(JsonNode.Parse(MessagePackSerializer.ConvertToJson(bytes)), true);
    }

    public override Asset CompileSource(Stream stream)
    {
        return new JsonAsset(JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }), _packed);
    }

    public string ToJson(bool indented = true)
    {
        return Data?.ToJsonString(new JsonSerializerOptions { WriteIndented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
    }

    public override void SaveTo(string path)
    {
        path = SafePath.PrepareFile(path);
        File.WriteAllText(path, ToJson());
    }
}
