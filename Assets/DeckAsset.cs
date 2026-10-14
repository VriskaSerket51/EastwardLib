using System.Globalization;
using System.Text.Json.Nodes;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

/// <summary>A virtual deck retains its definition and the textures owned by its parent pack.</summary>
public sealed class DeckAsset : JsonAsset
{
    private sealed record Vertex(double X, double Y, double U, double V);
    public IReadOnlyDictionary<string, RgbaImage> Textures
    {
        get;
    }
    public IReadOnlyList<string> Entries
    {
        get
        {
            if (Data?["meshes"] is JsonArray meshes)
            {
                return new[] { "Composite" }.Concat(Enumerable.Range(1, meshes.Count).Select(index => "Part " + index)).ToArray();
            }

            if (Data?["groups"] is JsonArray groups)
            {
                return groups.SelectMany(group => group!["tiles"]!.AsArray()).Select(tile => tile!["name"]!.ToString()).ToArray();
            }

            if (Data?["quads"] is JsonArray quads)
            {
                return quads.Select(quad => quad!["index"]!.ToString()).ToArray();
            }

            return [];
        }
    }

    public DeckAsset(JsonNode definition, IReadOnlyDictionary<string, RgbaImage> textures) : base(definition)
    {
        Textures = textures;
    }

    public RgbaImage? Render(string? entry = null, string? channel = null)
    {
        channel ??= Textures.Keys.FirstOrDefault();
        if (channel == null || !Textures.TryGetValue(channel, out var texture))
        {
            return null;
        }

        var meshes = Meshes(entry).Select(MeshVertices).ToArray();
        if (meshes.Length == 0)
        {
            return null;
        }

        var vertices = meshes.SelectMany(mesh => mesh).ToArray();
        var left = checked((int)Math.Floor(Math.Min(0, vertices.Min(vertex => vertex.X))));
        var top = checked((int)Math.Floor(Math.Min(0, vertices.Min(vertex => vertex.Y))));
        var right = checked((int)Math.Ceiling(Math.Max(1, vertices.Max(vertex => vertex.X))));
        var bottom = checked((int)Math.Ceiling(Math.Max(1, vertices.Max(vertex => vertex.Y))));
        var width = checked(right - left);
        var height = checked(bottom - top);
        if ((long)width * height > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Deck preview exceeds the image size limit");
        }

        var image = new RgbaImage(width, height, new byte[checked(width * height * 4)]);
        foreach (var mesh in meshes)
        {
            var x0 = Math.Max(left, (int)Math.Floor(mesh.Min(vertex => vertex.X)));
            var y0 = Math.Max(top, (int)Math.Floor(mesh.Min(vertex => vertex.Y)));
            var x1 = Math.Min(right, (int)Math.Ceiling(mesh.Max(vertex => vertex.X)));
            var y1 = Math.Min(bottom, (int)Math.Ceiling(mesh.Max(vertex => vertex.Y)));
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    if (!Sample(mesh[3], mesh[1], mesh[0], x + 0.5, y + 0.5, out var u, out var v) && !Sample(mesh[3], mesh[2], mesh[1], x + 0.5, y + 0.5, out u, out v))
                    {
                        continue;
                    }

                    var sx = Math.Clamp((int)Math.Floor(u * texture.Width), 0, texture.Width - 1);
                    var sy = Math.Clamp((int)Math.Floor(v * texture.Height), 0, texture.Height - 1);
                    var source = (sy * texture.Width + sx) * 4;
                    var target = ((y - top) * width + x - left) * 4;
                    var alpha = texture.Pixels[source + 3] / 255.0;
                    var previous = image.Pixels[target + 3] / 255.0;
                    var combined = alpha + previous * (1 - alpha);
                    if (combined == 0)
                    {
                        continue;
                    }

                    for (var color = 0; color < 3; color++)
                    {
                        image.Pixels[target + color] = (byte)Math.Clamp(Math.Round((texture.Pixels[source + color] * alpha + image.Pixels[target + color] * previous * (1 - alpha)) / combined), 0, 255);
                    }

                    image.Pixels[target + 3] = (byte)Math.Clamp(Math.Round(combined * 255), 0, 255);
                }
            }
        }
        return image;
    }

    private IEnumerable<JsonNode> Meshes(string? entry)
    {
        if (Data?["meshes"] is JsonArray meshes)
        {
            if (entry == null || entry == "Composite")
            {
                return meshes.Select(mesh => mesh!);
            }

            if (entry.StartsWith("Part ", StringComparison.Ordinal) && int.TryParse(entry[5..], out var part) && part > 0 && part <= meshes.Count)
            {
                return [meshes[part - 1]!];
            }

            throw new ArgumentException("Unknown deck part", nameof(entry));
        }
        if (Data?["groups"] is JsonArray groups)
        {
            var tiles = groups.SelectMany(group => group!["tiles"]!.AsArray());
            var tile = entry == null ? tiles.FirstOrDefault() : tiles.FirstOrDefault(tile => tile!["name"]!.ToString() == entry);
            return tile?["meshes"]?.AsArray().Select(mesh => mesh!) ?? [];
        }
        if (Data?["quads"] is JsonArray quads)
        {
            var quad = entry == null ? quads.FirstOrDefault() : quads.FirstOrDefault(quad => quad!["index"]!.ToString() == entry);
            if (quad == null)
            {
                return [];
            }

            var rect = quad["rect"]!;
            var uv = quad["uv"]!;
            var x0 = Number(rect[0]);
            var y0 = Number(rect[1]);
            var x1 = Number(rect[2]);
            var y1 = Number(rect[3]);
            var u0 = Number(uv[0]);
            var v0 = Number(uv[1]);
            var u1 = Number(uv[2]);
            var v1 = Number(uv[3]);
            return [new JsonObject { ["verts"] = new JsonArray(new JsonArray(x0, y0), new JsonArray(x1, y0), new JsonArray(x1, y1), new JsonArray(x0, y1)), ["uv"] = new JsonArray(new JsonArray(u0, v0), new JsonArray(u1, v0), new JsonArray(u1, v1), new JsonArray(u0, v1)) }];
        }
        return [];
    }

    private static Vertex[] MeshVertices(JsonNode mesh)
    {
        var positions = mesh["verts"]!.AsArray();
        var uvs = mesh["uv"]!.AsArray();
        if (positions.Count != 4 || uvs.Count != 4)
        {
            throw new InvalidDataException("Expected four deck mesh vertices");
        }
        // MOAI uses Y-up world coordinates and top-down texture coordinates.
        // This preview projects XY; scene cameras and depth shading are not applied.
        return Enumerable.Range(0, 4).Select(index => new Vertex(Number(positions[index]![0]), -Number(positions[index]![1]), Number(uvs[index]![0]), Number(uvs[index]![1]))).ToArray();
    }

    private static bool Sample(Vertex a, Vertex b, Vertex c, double x, double y, out double u, out double v)
    {
        var determinant = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        u = v = 0;
        if (Math.Abs(determinant) < 0.000000001)
        {
            return false;
        }

        var w0 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / determinant;
        var w1 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / determinant;
        var w2 = 1 - w0 - w1;
        if (w0 < -0.000000001 || w1 < -0.000000001 || w2 < -0.000000001)
        {
            return false;
        }

        u = w0 * a.U + w1 * b.U + w2 * c.U;
        v = w0 * a.V + w1 * b.V + w2 * c.V;
        return true;
    }

    private static double Number(JsonNode? value)
    {
        var number = double.Parse(value!.ToString(), CultureInfo.InvariantCulture);
        if (!double.IsFinite(number))
        {
            throw new InvalidDataException("Invalid deck coordinate");
        }

        return number;
    }
}
