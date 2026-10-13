using System.IO.Compression;
using System.Text;
using EastwardLib.Utils;

namespace EastwardLib.Compilation;

public static class AseReader
{
    public sealed record Layer(string Name, int Level, bool Group, bool Background, string Feature);
    public sealed record Cel(int Layer, int X, int Y, RgbaImage Image);
    public sealed record Frame(int Duration, IReadOnlyList<Cel> Cels);
    public sealed record Document(IReadOnlyList<Layer> Layers, IReadOnlyList<Frame> Frames, IReadOnlyList<AseWriter.Tag> Tags);

    public static Document Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var size = reader.ReadUInt32();
        if (size != stream.Length || reader.ReadUInt16() != 0xa5e0)
        {
            throw new InvalidDataException("Invalid ASE header");
        }

        var count = reader.ReadUInt16();
        reader.ReadUInt16();
        reader.ReadUInt16();
        if (reader.ReadUInt16() != 32)
        {
            throw new NotSupportedException("Convert ASE documents to 32-bit RGBA before importing");
        }

        stream.Position = 128;
        var layers = new List<Layer>();
        var frames = new List<Frame>();
        var tags = new List<AseWriter.Tag>();
        var featureGroups = new Dictionary<int, string>();
        for (var frameIndex = 0; frameIndex < count; frameIndex++)
        {
            var frameEnd = checked(stream.Position + reader.ReadUInt32());
            if (frameEnd > stream.Length || reader.ReadUInt16() != 0xf1fa)
            {
                throw new InvalidDataException("Invalid ASE frame");
            }

            var oldChunks = reader.ReadUInt16();
            var duration = reader.ReadUInt16();
            reader.ReadBytes(2);
            var newChunks = reader.ReadUInt32();
            var chunks = oldChunks == 65535 ? newChunks : oldChunks;
            var cels = new List<Cel>();
            for (var chunkIndex = 0; chunkIndex < chunks; chunkIndex++)
            {
                var end = checked(stream.Position + reader.ReadUInt32());
                if (end < stream.Position + 2 || end > frameEnd)
                {
                    throw new InvalidDataException("Invalid ASE chunk size");
                }

                var type = reader.ReadUInt16();
                if (type == 0x2004)
                {
                    var flags = reader.ReadUInt16();
                    var kind = reader.ReadUInt16();
                    var level = reader.ReadUInt16();
                    reader.ReadBytes(4);
                    var blend = reader.ReadUInt16();
                    var opacity = reader.ReadByte();
                    reader.ReadBytes(3);
                    var name = String(reader).Trim();
                    if (kind is not (0 or 1))
                    {
                        throw new NotSupportedException("Rasterize ASE tilemap layers before importing");
                    }

                    if (blend != 0 || opacity != 255)
                    {
                        throw new NotSupportedException("Rasterize ASE blend modes and layer opacity before importing");
                    }

                    foreach (var key in featureGroups.Keys.Where(key => key >= level).ToArray())
                    {
                        featureGroups.Remove(key);
                    }

                    var parent = featureGroups.GetValueOrDefault(level - 1) ?? "";
                    var feature = name.StartsWith('#') ? (parent.Length == 0 ? name[1..] : parent + "." + name[1..]) : parent;
                    if (kind == 1)
                    {
                        featureGroups[level] = feature;
                    }

                    layers.Add(new Layer(name, level, kind == 1, (flags & 8) != 0, feature));
                }
                else if (type == 0x2005)
                {
                    var layer = reader.ReadUInt16();
                    var x = reader.ReadInt16();
                    var y = reader.ReadInt16();
                    var opacity = reader.ReadByte();
                    var kind = reader.ReadUInt16();
                    reader.ReadBytes(7);
                    RgbaImage image;
                    if (kind == 1)
                    {
                        var linked = reader.ReadUInt16();
                        if (linked >= frames.Count)
                        {
                            throw new InvalidDataException("Invalid linked ASE cel");
                        }

                        image = frames[linked].Cels.Single(cel => cel.Layer == layer).Image;
                    }
                    else if (kind is 0 or 2)
                    {
                        var width = reader.ReadUInt16();
                        var height = reader.ReadUInt16();
                        var pixels = new byte[checked(width * height * 4)];
                        if (kind == 0)
                        {
                            reader.BaseStream.ReadExactly(pixels);
                        }
                        else
                        {
                            using var compressed = new MemoryStream(reader.ReadBytes(checked((int)(end - stream.Position))));
                            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
                            zlib.ReadExactly(pixels);
                            if (zlib.ReadByte() != -1)
                            {
                                throw new InvalidDataException("ASE cel exceeds dimensions");
                            }
                        }
                        image = new RgbaImage(width, height, pixels);
                    }
                    else
                    {
                        throw new NotSupportedException("Unsupported ASE cel type: " + kind);
                    }

                    if (layer >= layers.Count)
                    {
                        throw new InvalidDataException("Invalid ASE cel layer");
                    }

                    if (opacity != 255)
                    {
                        var pixels = (byte[])image.Pixels.Clone();
                        for (var pixel = 3; pixel < pixels.Length; pixel += 4)
                        {
                            pixels[pixel] = (byte)((pixels[pixel] * opacity + 127) / 255);
                        }

                        image = new RgbaImage(image.Width, image.Height, pixels);
                    }
                    cels.Add(new Cel(layer, x, y, image));
                }
                else if (type == 0x2018)
                {
                    var tagCount = reader.ReadUInt16();
                    reader.ReadBytes(8);
                    for (var index = 0; index < tagCount; index++)
                    {
                        var from = reader.ReadUInt16();
                        var to = reader.ReadUInt16();
                        var direction = reader.ReadByte();
                        reader.ReadUInt16();
                        reader.ReadBytes(6);
                        reader.ReadBytes(4);
                        var name = String(reader);
                        if (from > to || to >= count)
                        {
                            throw new InvalidDataException("Invalid ASE tag range");
                        }

                        if (direction != 0)
                        {
                            throw new NotSupportedException("Convert ASE reverse or ping-pong tags to explicit frames");
                        }

                        tags.Add(new AseWriter.Tag(name, from, to));
                    }
                }
                if (stream.Position > end)
                {
                    throw new InvalidDataException("ASE chunk exceeds declared size");
                }

                stream.Position = end;
            }
            if (stream.Position != frameEnd)
            {
                throw new InvalidDataException("ASE frame size differs");
            }

            frames.Add(new Frame(duration, cels.OrderBy(cel => cel.Layer).ToArray()));
        }
        if (frames.Count == 0)
        {
            throw new InvalidDataException("Empty ASE document");
        }

        return new Document(layers, frames, tags);
    }

    private static string String(BinaryReader reader)
    {
        var size = reader.ReadUInt16();
        var bytes = reader.ReadBytes(size);
        if (bytes.Length != size)
        {
            throw new EndOfStreamException();
        }

        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
