using System.Text;
using System.Text.Json.Nodes;
using EastwardLib.MetaData;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class LocalePackAsset : Asset
{
    public JsonNode Config
    {
        get;
    }

    public Dictionary<string, JsonNode> Languages { get; } = new(StringComparer.Ordinal);

    public LocalePackAsset(AssetManager manager, AssetInfo info)
    {
        Config = manager.RequireJson(info.ObjectFiles["config"]);
        var directory = info.ObjectFiles["data"];
        foreach (var name in manager.GetFiles(directory).Where(name => !name.Contains('/')))
        {
            var source = Encoding.UTF8.GetString(manager.RequireFile(directory + "/" + name));
            Languages.Add(name, LuaTable.Parse(source) ?? throw new InvalidDataException("Empty locale table"));
        }
    }

    public override byte[] Encode()
    {
        throw new NotSupportedException("Locale packs contain configuration and language files; use language tables individually");
    }

    public override void Decode(Stream stream)
    {
        throw new NotSupportedException("Load locale packs through AssetManager");
    }

    public override void SaveTo(string path)
    {
        new JsonAsset(Config).SaveTo(SafePath.ResolveWithin(path, "locale_pack.json"));
        foreach (var (language, table) in Languages)
        {
            foreach (var (name, messages) in table.AsObject())
            {
                var po = new StringBuilder("msgid \"\"\nmsgstr \"\"\n\"Content-Type: text/plain; charset=UTF-8\\n\"\n\n");
                foreach (var (id, value) in messages!.AsObject())
                {
                    po.Append("msgid ").Append(Quote(id)).Append("\nmsgstr ").Append(Quote(value?.ToString() ?? "")).Append("\n\n");
                }

                var text = new TextAsset(po.ToString());
                text.SaveTo(SafePath.ResolveWithin(path, "locales/" + language + "/" + name + ".po"));
                if (language == Config["source_locale"]?.ToString())
                {
                    text.SaveTo(SafePath.ResolveWithin(path, "templates/" + name + ".pot"));
                }
            }
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
    }
}
