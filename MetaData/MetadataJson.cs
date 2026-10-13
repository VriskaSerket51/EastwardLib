using System.Text.Json;
using System.Text.Json.Nodes;
using EastwardLib.Utils;

namespace EastwardLib.MetaData;

internal static class MetadataJson
{
    public static JsonObject Parse(string json, string name)
    {
        try
        {
            return Object(JsonNode.Parse(json), name);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid " + name + " JSON", error);
        }
    }

    public static JsonObject Object(JsonNode? node, string name)
    {
        return node as JsonObject ?? throw new InvalidDataException("Expected an object at " + name);
    }

    public static string? Text(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    public static string RequiredText(JsonNode? node, string name)
    {
        var value = Text(node);
        return !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException("Expected a nonempty string at " + name);
    }

    public static string Reference(JsonNode? node, string name)
    {
        if (node is JsonValue value && (value.TryGetValue<string>(out _) || value.TryGetValue<int>(out _) || value.TryGetValue<long>(out _)))
        {
            return node.ToString();
        }
        throw new InvalidDataException("Invalid reference at " + name);
    }

    public static string Serialize(JsonObject data, bool indented)
    {
        return data.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });
    }

    public static void Save(string path, string json)
    {
        File.WriteAllText(SafePath.PrepareFile(path), json);
    }

    public static void Replace(JsonObject destination, JsonObject source)
    {
        var copy = (JsonObject)source.DeepClone();
        destination.Clear();
        foreach (var (key, value) in copy.ToArray())
        {
            copy.Remove(key);
            destination.Add(key, value);
        }
    }

    public static bool Equivalent(JsonNode? left, JsonNode? right)
    {
        if (JsonNode.DeepEquals(left, right))
        {
            return true;
        }
        if (left is JsonObject a && right is JsonObject b)
        {
            return a.Count == b.Count && a.All(pair => b.ContainsKey(pair.Key) && Equivalent(pair.Value, b[pair.Key]));
        }
        if (left is JsonArray x && right is JsonArray y)
        {
            return x.Count == y.Count && x.Zip(y).All(pair => Equivalent(pair.First, pair.Second));
        }
        if (left is JsonValue first && right is JsonValue second && first.TryGetValue<double>(out var firstNumber) && second.TryGetValue<double>(out var secondNumber))
        {
            // JSON decimal spellings can differ while denoting the same native double.
            var floating = first.ToJsonString().IndexOfAny(['.', 'e', 'E']) >= 0 || second.ToJsonString().IndexOfAny(['.', 'e', 'E']) >= 0;
            return floating && firstNumber.Equals(secondNumber);
        }
        return false;
    }
}
