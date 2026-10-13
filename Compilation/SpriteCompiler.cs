using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EastwardLib.Utils;

namespace EastwardLib.Compilation;

public static class SpriteCompiler
{
    public sealed record Result(JsonNode Definition, RgbaImage Atlas);

    public static Result Compile(string path, JsonNode original, string atlasId, string atlasName, int width, int height, RgbaImage? originalAtlas = null)
    {
        var definition = original.DeepClone();
        var modules = new JsonObject();
        var frames = new JsonObject();
        var animations = new JsonObject();
        var features = original["features"]?.DeepClone() as JsonArray ?? new JsonArray();
        var pixels = new byte[checked(width * height * 4)];
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        var images = new Dictionary<string, RgbaImage>(StringComparer.Ordinal);
        var moduleImages = new Dictionary<string, string>(StringComparer.Ordinal);
        var folder = Directory.Exists(path);
        var files = folder ? Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Where(file => Path.GetExtension(file).ToLowerInvariant() is ".ase" or ".aseprite").Order(StringComparer.Ordinal).ToArray() : [path];
        if (files.Length == 0)
        {
            throw new InvalidDataException("Missing ASE animations");
        }

        var originalAnimations = original["anims"]!.AsObject().Select(pair => pair.Value!).ToArray();
        foreach (var file in files)
        {
            var document = AseReader.Read(file);
            var filename = Path.GetFileNameWithoutExtension(file);
            var name = originalAnimations.FirstOrDefault(value => !value["name"]!.ToString().Contains(':') && ProjectPath.Escape(value["name"]!.ToString()) == filename)?["name"]!.ToString() ?? filename;
            var sequence = new JsonArray();
            var originalSequence = originalAnimations.FirstOrDefault(animation => animation["name"]?.ToString() == name)?["seq"] as JsonArray;
            var ox = 0;
            var oy = 0;
            foreach (var frame in document.Frames)
            {
                var origin = frame.Cels.FirstOrDefault(cel => document.Layers[cel.Layer].Name == "@origin");
                if (origin != null && Bounds(origin.Image) is { } originBounds)
                {
                    ox = origin.X + originBounds.X;
                    oy = origin.Y + originBounds.Y + originBounds.Height;
                }
                var parts = new JsonArray();
                var anchors = new JsonObject();
                foreach (var cel in frame.Cels)
                {
                    var layer = document.Layers[cel.Layer];
                    if (layer.Background || layer.Name.StartsWith("//", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (layer.Name.StartsWith("@anchor:", StringComparison.Ordinal))
                    {
                        var points = new JsonArray();
                        for (var py = 0; py < cel.Image.Height; py++)
                        {
                            for (var px = 0; px < cel.Image.Width; px++)
                            {
                                var offset = (py * cel.Image.Width + px) * 4;
                                if (cel.Image.Pixels[offset + 3] == 0)
                                {
                                    continue;
                                }

                                var color = "#" + Convert.ToHexString(cel.Image.Pixels.AsSpan(offset, 3)).ToLowerInvariant();
                                points.Add(new JsonArray(JsonValue.Create(cel.X + px - ox), JsonValue.Create(cel.Y + py - oy), JsonValue.Create(color)));
                            }
                        }

                        anchors.Add(layer.Name[8..], points);
                        continue;
                    }
                    if (layer.Name.StartsWith('@') || Bounds(cel.Image) is not { } bounds)
                    {
                        continue;
                    }

                    var image = cel.Image.Crop(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    var featureId = 0;
                    if (layer.Feature.Length > 0)
                    {
                        var feature = features.FirstOrDefault(value => value?["name"]?.ToString() == layer.Feature);
                        if (feature == null)
                        {
                            featureId = features.Select(value => int.Parse(value!["id"]!.ToString())).DefaultIfEmpty(0).Max() + 1;
                            features.Add(new JsonObject { ["id"] = featureId, ["name"] = layer.Feature });
                        }
                        else
                        {
                            featureId = int.Parse(feature["id"]!.ToString());
                        }
                    }
                    var imageKey = image.Width + ":" + image.Height + ":" + Convert.ToHexString(SHA256.HashData(image.Pixels));
                    var key = featureId + ":" + imageKey;
                    if (!cache.TryGetValue(key, out var id))
                    {
                        id = (modules.Count + 1).ToString();
                        modules[id] = new JsonObject { ["atlas"] = atlasId, ["feature"] = featureId };
                        cache.Add(key, id);
                        images.TryAdd(imageKey, image);
                        moduleImages.Add(id, imageKey);
                    }
                    parts.Add(new JsonArray(JsonValue.Create(id), JsonValue.Create(cel.X + bounds.X - ox), JsonValue.Create(cel.Y + bounds.Y - oy)));
                }
                var frameId = (frames.Count + 1).ToString();
                var previousId = sequence.Count < originalSequence?.Count ? originalSequence[sequence.Count]?[0]?.ToString() : null;
                var frameData = previousId != null ? original["frames"]?[previousId]?.DeepClone() as JsonObject : null;
                frameData ??= new JsonObject { ["name"] = "" };
                var meta = frameData["meta"]?.DeepClone() as JsonObject ?? new JsonObject { ["glow_sum"] = 0 };
                meta["origin"] = new JsonArray(JsonValue.Create(ox), JsonValue.Create(oy));
                meta["anchors"] = anchors;
                frameData["parts"] = parts;
                frameData["meta"] = meta;
                frames[frameId] = frameData;
                sequence.Add(new JsonArray(JsonValue.Create(frameId), JsonValue.Create(frame.Duration / 1000.0), JsonValue.Create(0), JsonValue.Create(0)));
            }
            void Animation(string animationName, JsonArray entries, bool deprecated)
            {
                var occurrence = animations.Count(pair => pair.Value?["name"]?.ToString() == animationName);
                var data = originalAnimations.Where(value => value["name"]?.ToString() == animationName).Skip(occurrence).FirstOrDefault()?.DeepClone() as JsonObject ?? new JsonObject();
                data["name"] = animationName;
                data["seq"] = entries;
                data["src_type"] = "ase";
                data["deprecated"] = deprecated;
                animations[(animations.Count + 1).ToString()] = data;
            }
            if (folder || document.Tags.Count == 0)
            {
                Animation(name, sequence.DeepClone().AsArray(), false);
            }

            foreach (var tag in document.Tags)
            {
                if (tag.Name.StartsWith("//", StringComparison.Ordinal) && !tag.Name.StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                var tagName = tag.Name.StartsWith("///", StringComparison.Ordinal) ? tag.Name[3..] : tag.Name;
                Animation(folder ? name + ":" + tagName : tagName, new JsonArray(sequence.Skip(tag.From).Take(tag.To - tag.From + 1).Select(entry => entry!.DeepClone()).ToArray()), tag.Name.StartsWith("///", StringComparison.Ordinal));
            }
        }
        var free = new List<Rectangle> { new(0, 0, width, height) };
        var locations = new Dictionary<string, Rectangle>(StringComparer.Ordinal);
        if (originalAtlas != null)
        {
            foreach (var (_, module) in original["modules"]!.AsObject())
            {
                if (module!["atlas"]?.ToString() != atlasId)
                {
                    continue;
                }

                var rect = module["rect"]!.AsArray().Select(value => int.Parse(value!.ToString())).ToArray();
                var image = originalAtlas.Crop(rect[0], rect[1], rect[2], rect[3]);
                if (Bounds(image) is not { } bounds)
                {
                    continue;
                }

                image = image.Crop(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                var key = image.Width + ":" + image.Height + ":" + Convert.ToHexString(SHA256.HashData(image.Pixels));
                locations.TryAdd(key, new Rectangle(rect[0] + bounds.X, rect[1] + bounds.Y, bounds.Width, bounds.Height));
            }
        }
        var keepLayout = images.Keys.All(locations.ContainsKey);
        if (keepLayout && originalAtlas != null)
        {
            originalAtlas.Pixels.CopyTo(pixels, 0);
        }

        foreach (var (key, image) in images.OrderByDescending(pair => (long)pair.Value.Width * pair.Value.Width + (long)pair.Value.Height * pair.Value.Height))
        {
            var w = image.Width + 2;
            var h = image.Height + 2;
            if (keepLayout)
            {
                continue;
            }

            var slot = free.Where(rect => rect.Width >= w && rect.Height >= h).OrderBy(rect => Math.Min(rect.Width - w, rect.Height - h)).ThenBy(rect => Math.Max(rect.Width - w, rect.Height - h)).FirstOrDefault()
                ?? throw new InvalidDataException("Sprite modules exceed the indexed atlas capacity");
            var used = new Rectangle(slot.X, slot.Y, w, h);
            var remaining = new List<Rectangle>();
            foreach (var rect in free)
            {
                if (used.Right <= rect.X || used.X >= rect.Right || used.Bottom <= rect.Y || used.Y >= rect.Bottom)
                {
                    remaining.Add(rect);
                    continue;
                }
                if (used.X > rect.X)
                {
                    remaining.Add(new Rectangle(rect.X, rect.Y, used.X - rect.X, rect.Height));
                }

                if (used.Right < rect.Right)
                {
                    remaining.Add(new Rectangle(used.Right, rect.Y, rect.Right - used.Right, rect.Height));
                }

                if (used.Y > rect.Y)
                {
                    remaining.Add(new Rectangle(rect.X, rect.Y, rect.Width, used.Y - rect.Y));
                }

                if (used.Bottom < rect.Bottom)
                {
                    remaining.Add(new Rectangle(rect.X, used.Bottom, rect.Width, rect.Bottom - used.Bottom));
                }
            }
            free = remaining.Distinct().Where(rect => !remaining.Any(other => other != rect && other.Contains(rect))).ToList();
            var x = used.X + 1;
            var y = used.Y + 1;
            for (var py = -1; py <= image.Height; py++)
            {
                for (var px = -1; px <= image.Width; px++)
                {
                    var source = (Math.Clamp(py, 0, image.Height - 1) * image.Width + Math.Clamp(px, 0, image.Width - 1)) * 4;
                    image.Pixels.AsSpan(source, 4).CopyTo(pixels.AsSpan(((y + py) * width + x + px) * 4));
                }
            }

            locations[key] = new Rectangle(x, y, image.Width, image.Height);
        }
        foreach (var (id, key) in moduleImages)
        {
            var rect = locations[key];
            modules[id]!["rect"] = new JsonArray(JsonValue.Create(rect.X), JsonValue.Create(rect.Y), JsonValue.Create(rect.Width), JsonValue.Create(rect.Height));
        }
        definition["atlases"] = new JsonObject { [atlasId] = atlasName };
        definition["modules"] = modules;
        definition["frames"] = frames;
        definition["anims"] = animations;
        definition["features"] = features;
        return new Result(definition, new RgbaImage(width, height, pixels));
    }

    private sealed record Rectangle(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public bool Contains(Rectangle other)
        {
            return X <= other.X && Y <= other.Y && Right >= other.Right && Bottom >= other.Bottom;
        }
    }

    private static (int X, int Y, int Width, int Height)? Bounds(RgbaImage image)
    {
        var left = image.Width;
        var top = image.Height;
        var right = 0;
        var bottom = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image.Pixels[(y * image.Width + x) * 4 + 3] == 0)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }
        }

        return right <= left || bottom <= top ? null : (left, top, right - left, bottom - top);
    }
}
