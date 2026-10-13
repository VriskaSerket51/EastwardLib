using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace EastwardLib.Utils;

public static class AseWriter
{
    private sealed record Part(RgbaImage Image, int X, int Y, string Feature);
    private sealed record Frame(Part[] Parts, int Duration, JsonObject? Anchors);
    public sealed record Tag(string Name, int From, int To);

    public static byte[] Write(JsonNode definition, IReadOnlyDictionary<string, RgbaImage> atlases, IReadOnlyList<Tag>? sourceTags = null)
    {
        var frames = new List<Frame>();
        var tags = new List<Tag>();
        var minX = 0;
        var minY = 0;
        var maxX = 1;
        var maxY = 1;
        foreach (var (_, animation) in definition["anims"]!.AsObject())
        {
            var first = frames.Count;
            foreach (var item in animation!["seq"]!.AsArray())
            {
                var source = definition["frames"]![item![0]!.ToString()] ?? throw new InvalidDataException("Missing sprite frame");
                var parts = source["parts"]!.AsArray().Select(part =>
                {
                    var module = definition["modules"]![part![0]!.ToString()] ?? throw new InvalidDataException("Missing sprite module");
                    var rect = module["rect"]!.AsArray().Select(Integer).ToArray();
                    var key = module["atlas"]?.ToString();
                    var atlas = key != null && atlases.TryGetValue(key, out var image) ? image
                        : key == null && atlases.Count == 1 ? atlases.Values.First() : throw new InvalidDataException("Missing sprite atlas: " + key);
                    var x = Integer(part[1]) + (item.AsArray().Count > 2 ? Integer(item[2]) : 0);
                    var y = Integer(part[2]) + (item.AsArray().Count > 3 ? Integer(item[3]) : 0);
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x + rect[2]);
                    maxY = Math.Max(maxY, y + rect[3]);
                    return new Part(atlas.Crop(rect[0], rect[1], rect[2], rect[3]), x, y, module["feature"]?.ToString() ?? "0");
                }).ToArray();
                var duration = checked((int)Math.Round(double.Parse(item[1]!.ToString(), CultureInfo.InvariantCulture) * 1000, MidpointRounding.AwayFromZero));
                if (duration is < 0 or > 65535)
                {
                    throw new InvalidDataException("Unsupported ASE frame duration");
                }

                frames.Add(new Frame(parts, duration, source["meta"]?["anchors"] as JsonObject));
            }
            if (frames.Count > first)
            {
                tags.Add(new Tag((animation["deprecated"]?.ToString() is "true" or "True" ? "///" : "") + animation["name"]!.ToString(), first, frames.Count - 1));
            }
        }
        if (sourceTags != null)
        {
            tags = sourceTags.ToList();
            if (tags.Any(tag => tag.From < 0 || tag.To < tag.From || tag.To >= frames.Count))
            {
                throw new InvalidDataException("Invalid ASE tag range");
            }
        }
        var width = maxX - minX;
        var height = maxY - minY;
        if (frames.Count is 0 or > 65535 || width > 32767 || height > 32767)
        {
            throw new InvalidDataException("Unsupported ASE document size");
        }

        var chunks = new List<byte[]>();
        var layerIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        var layerCount = 0;
        int Layer(string name, int level = 0, bool group = false)
        {
            var body = new byte[16];
            U16(body, 0, 3);
            U16(body, 2, group ? 1 : 0);
            U16(body, 4, level);
            body[12] = 255;
            chunks.Add(Chunk(0x2004, Combine(body, String(name))));
            return layerCount++;
        }
        var keys = frames.SelectMany(frame => frame.Parts.Select((part, index) => index + ":" + part.Feature)).Distinct(StringComparer.Ordinal).OrderBy(key => int.Parse(key.Split(':')[0])).ToArray();
        foreach (var key in keys)
        {
            var feature = definition["features"] as JsonArray;
            var name = feature?.FirstOrDefault(value => value?["id"]?.ToString() == key.Split(':')[1])?["name"]?.ToString() ?? "";
            var names = name.Split('.');
            if (name.Length > 0)
            {
                for (var level = 0; level < names.Length - 1; level++)
                {
                    Layer("#" + names[level], level, true);
                }
            }

            layerIndices.Add(key, Layer(name.Length == 0 ? "part_" + layerIndices.Count : "#" + names[^1], name.Length == 0 ? 0 : names.Length - 1));
        }
        var origin = Layer("@origin");
        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            foreach (var name in frame.Anchors?.Select(pair => pair.Key) ?? [])
            {
                if (!anchors.ContainsKey(name))
                {
                    anchors.Add(name, Layer("@anchor:" + name));
                }
            }
        }

        if (layerCount > 65535)
        {
            throw new InvalidDataException("Too many ASE layers");
        }

        using var tagData = new MemoryStream();
        tagData.Write(new byte[10]);
        foreach (var tag in tags)
        {
            var data = new byte[17];
            U16(data, 0, tag.From);
            U16(data, 2, tag.To);
            tagData.Write(data);
            tagData.Write(String(tag.Name));
        }
        var tagBytes = tagData.ToArray();
        U16(tagBytes, 0, tags.Count);
        chunks.Add(Chunk(0x2018, tagBytes));
        using var output = new MemoryStream();
        output.Write(new byte[128]);
        var marker = new RgbaImage(1, 1, [255, 255, 255, 255]);
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var frameChunks = i == 0 ? new List<byte[]>(chunks) : [];
            for (var p = 0; p < frame.Parts.Length; p++)
            {
                var part = frame.Parts[p];
                frameChunks.Add(Cel(layerIndices[p + ":" + part.Feature], part.X - minX, part.Y - minY, part.Image));
            }
            frameChunks.Add(Cel(origin, -minX, -minY - 1, marker));
            foreach (var (name, data) in frame.Anchors ?? [])
            {
                var points = data as JsonArray;
                if (points == null || points.Count == 0)
                {
                    continue;
                }

                var left = points.Min(point => Integer(point![0]));
                var top = points.Min(point => Integer(point![1]));
                var w = points.Max(point => Integer(point![0])) - left + 1;
                var h = points.Max(point => Integer(point![1])) - top + 1;
                var pixels = new byte[checked(w * h * 4)];
                foreach (var point in points)
                {
                    var offset = ((Integer(point![1]) - top) * w + Integer(point[0]) - left) * 4;
                    var color = point[2]!.ToString();
                    for (var c = 0; c < 3; c++)
                    {
                        pixels[offset + c] = byte.Parse(color.AsSpan(1 + c * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    }

                    pixels[offset + 3] = 255;
                }
                frameChunks.Add(Cel(anchors[name], left - minX, top - minY, new RgbaImage(w, h, pixels)));
            }
            var header = new byte[16];
            U32(header, 0, checked(16 + frameChunks.Sum(chunk => chunk.Length)));
            U16(header, 4, 0xf1fa);
            U16(header, 6, frameChunks.Count >= 65535 ? 65535 : frameChunks.Count);
            U16(header, 8, frame.Duration);
            U32(header, 12, frameChunks.Count);
            output.Write(header);
            foreach (var chunk in frameChunks)
            {
                output.Write(chunk);
            }
        }
        var result = output.ToArray();
        U32(result, 0, result.Length);
        U16(result, 4, 0xa5e0);
        U16(result, 6, frames.Count);
        U16(result, 8, width);
        U16(result, 10, height);
        U16(result, 12, 32);
        U32(result, 14, 1);
        U16(result, 18, 100);
        result[34] = result[35] = 1;
        return result;
    }

    private static byte[] Cel(int layer, int x, int y, RgbaImage image)
    {
        if (x is < short.MinValue or > short.MaxValue || y is < short.MinValue or > short.MaxValue || image.Width > 65535 || image.Height > 65535)
        {
            throw new InvalidDataException("ASE cel exceeds coordinate limits");
        }

        var header = new byte[20];
        U16(header, 0, layer);
        U16(header, 2, x);
        U16(header, 4, y);
        header[6] = 255;
        U16(header, 7, 2);
        U16(header, 16, image.Width);
        U16(header, 18, image.Height);
        using var data = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionLevel.Optimal, true))
        {
            zlib.Write(image.Pixels);
        }

        return Chunk(0x2005, Combine(header, data.ToArray()));
    }

    private static int Integer(JsonNode? node)
    {
        return node == null ? 0 : int.Parse(node.ToString(), CultureInfo.InvariantCulture);
    }

    private static byte[] String(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        if (data.Length > 65535)
        {
            throw new InvalidDataException("ASE string too long");
        }

        var result = new byte[2 + data.Length];
        U16(result, 0, data.Length);
        data.CopyTo(result, 2);
        return result;
    }

    private static byte[] Chunk(int type, byte[] bytes)
    {
        var result = new byte[6 + bytes.Length];
        U32(result, 0, result.Length);
        U16(result, 4, type);
        bytes.CopyTo(result, 6);
        return result;
    }

    private static byte[] Combine(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    private static void U16(byte[] bytes, int offset, int value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), unchecked((ushort)value));
    }

    private static void U32(byte[] bytes, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    }
}
