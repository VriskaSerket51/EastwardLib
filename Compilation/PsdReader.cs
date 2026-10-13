using System.IO.Compression;
using System.Text;
using EastwardLib.Utils;

namespace EastwardLib.Compilation;

public static class PsdReader
{
    private sealed record Record(string Name, int X, int Y, int Width, int Height, (short Id, int Size)[] Channels, int Divider, byte Opacity, byte Flags);

    public static RgbaImage LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        return LoadImage(stream);
    }

    public static RgbaImage LoadImage(Stream stream)
    {
        var bytes = EastwardLib.Assets.Asset.Create<EastwardLib.Assets.BinaryAsset>(stream).Encode();
        if (!bytes.AsSpan().StartsWith("8BPS"u8))
        {
            return AssetManager.DecodeImage(bytes);
        }
        using var buffer = new MemoryStream(bytes);
        var layers = Read(buffer);
        if (layers.Count != 1 || layers[0].Image == null || layers[0].X != 0 || layers[0].Y != 0)
        {
            throw new NotSupportedException("Flatten this texture PSD to one layer at the origin before importing");
        }

        return layers[0].Image!;
    }

    public static IReadOnlyList<PsdWriter.Layer> Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static IReadOnlyList<PsdWriter.Layer> Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "8BPS" || U16(reader) != 1)
        {
            throw new InvalidDataException("Invalid PSD header");
        }

        Skip(reader, 6);
        var channels = U16(reader);
        var height = U32(reader);
        var width = U32(reader);
        if (U16(reader) != 8 || U16(reader) != 3)
        {
            throw new NotSupportedException("PSD import requires 8-bit RGB layers");
        }

        Skip(reader, U32(reader));
        Skip(reader, U32(reader));
        var sectionSize = U32(reader);
        var sectionEnd = checked(stream.Position + sectionSize);
        if (sectionEnd > stream.Length)
        {
            throw new InvalidDataException("Invalid PSD layer section");
        }

        if (sectionSize == 0)
        {
            return [new PsdWriter.Layer("image", ReadComposite(reader, width, height, channels))];
        }

        var layerSize = U32(reader);
        if (layerSize == 0)
        {
            stream.Position = sectionEnd;
            return [new PsdWriter.Layer("image", ReadComposite(reader, width, height, channels))];
        }
        var layerEnd = checked(stream.Position + layerSize);
        if (layerEnd > sectionEnd || sectionEnd > stream.Length)
        {
            throw new InvalidDataException("Invalid PSD layer section");
        }

        var count = Math.Abs((int)unchecked((short)U16(reader)));
        var records = new List<Record>();
        for (var index = 0; index < count; index++)
        {
            var top = I32(reader);
            var left = I32(reader);
            var bottom = I32(reader);
            var right = I32(reader);
            var layerChannels = new (short, int)[U16(reader)];
            for (var channel = 0; channel < layerChannels.Length; channel++)
            {
                layerChannels[channel] = (unchecked((short)U16(reader)), U32(reader));
            }

            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "8BIM")
            {
                throw new InvalidDataException("Invalid PSD blend signature");
            }

            var blend = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var opacity = reader.ReadByte();
            reader.ReadByte();
            var flags = reader.ReadByte();
            reader.ReadByte();
            var extraSize = U32(reader);
            var extraEnd = checked(stream.Position + extraSize);
            if (extraEnd > layerEnd)
            {
                throw new InvalidDataException("PSD layer record exceeds section");
            }

            var maskSize = U32(reader);
            if (maskSize > 0)
            {
                throw new NotSupportedException("Apply PSD layer masks before importing");
            }

            Skip(reader, U32(reader));
            var nameSize = reader.ReadByte();
            var name = Encoding.Latin1.GetString(reader.ReadBytes(nameSize));
            Skip(reader, (4 - (nameSize + 1) % 4) % 4);
            var divider = 0;
            while (stream.Position + 12 <= extraEnd)
            {
                var signature = Encoding.ASCII.GetString(reader.ReadBytes(4));
                var key = Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (signature != "8BIM")
                {
                    throw new InvalidDataException("Invalid PSD additional info");
                }

                var size = U32(reader);
                var end = checked(stream.Position + size);
                if (end > extraEnd)
                {
                    throw new InvalidDataException("Invalid PSD additional info size");
                }

                if (key == "luni")
                {
                    var length = U32(reader);
                    if ((long)length * 2 > end - stream.Position)
                    {
                        throw new InvalidDataException("Invalid PSD Unicode name");
                    }

                    name = Encoding.BigEndianUnicode.GetString(reader.ReadBytes(checked(length * 2)));
                }
                else if (key == "lsct")
                {
                    divider = U32(reader);
                }
                stream.Position = end + size % 2;
            }
            stream.Position = extraEnd;
            if (divider == 0 && blend != "norm")
            {
                throw new NotSupportedException("Rasterize PSD blend mode before importing: " + blend);
            }

            records.Add(new Record(name, left, top, checked(right - left), checked(bottom - top), layerChannels, divider, opacity, flags));
        }
        var layers = new List<(Record Record, RgbaImage? Image)>();
        foreach (var record in records)
        {
            var pixels = record.Width > 0 && record.Height > 0 ? new byte[checked(record.Width * record.Height * 4)] : null;
            if (pixels != null)
            {
                for (var pixel = 3; pixel < pixels.Length; pixel += 4)
                {
                    pixels[pixel] = 255;
                }
            }

            foreach (var (id, size) in record.Channels)
            {
                var end = checked(stream.Position + size);
                if (size < 2 || end > layerEnd)
                {
                    throw new InvalidDataException("Invalid PSD channel size");
                }

                var mode = U16(reader);
                var expected = pixels == null ? 0 : pixels.Length / 4;
                byte[] plane;
                if (mode == 0)
                {
                    plane = reader.ReadBytes(expected);
                }
                else if (mode == 1)
                {
                    var lengths = Enumerable.Range(0, record.Height).Select(_ => (int)U16(reader)).ToArray();
                    using var decoded = new MemoryStream();
                    foreach (var length in lengths)
                    {
                        var row = reader.ReadBytes(length);
                        var start = decoded.Length;
                        for (var cursor = 0; cursor < row.Length;)
                        {
                            var control = unchecked((sbyte)row[cursor++]);
                            if (control >= 0)
                            {
                                var run = control + 1;
                                if (cursor + run > row.Length)
                                {
                                    throw new InvalidDataException("Invalid PSD RLE run");
                                }

                                decoded.Write(row.AsSpan(cursor, run));
                                cursor += run;
                            }
                            else if (control != -128)
                            {
                                if (cursor >= row.Length)
                                {
                                    throw new InvalidDataException("Invalid PSD RLE repeat");
                                }

                                var value = row[cursor++];
                                for (var repeat = 0; repeat < 1 - control; repeat++)
                                {
                                    decoded.WriteByte(value);
                                }
                            }
                        }
                        if (decoded.Length - start != record.Width)
                        {
                            throw new InvalidDataException("Invalid PSD RLE row width");
                        }
                    }
                    plane = decoded.ToArray();
                }
                else if (mode is 2 or 3)
                {
                    using var input = new MemoryStream(reader.ReadBytes(checked((int)(end - stream.Position))));
                    using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                    plane = new byte[expected];
                    zlib.ReadExactly(plane);
                    if (zlib.ReadByte() != -1)
                    {
                        throw new InvalidDataException("PSD channel exceeds dimensions");
                    }

                    if (mode == 3)
                    {
                        for (var row = 0; row < record.Height; row++)
                        {
                            for (var column = 1; column < record.Width; column++)
                            {
                                var offset = row * record.Width + column;
                                plane[offset] = unchecked((byte)(plane[offset] + plane[offset - 1]));
                            }
                        }
                    }
                }
                else
                {
                    throw new NotSupportedException("Unsupported PSD compression: " + mode);
                }

                if (plane.Length != expected || stream.Position > end)
                {
                    throw new InvalidDataException("PSD channel dimensions differ");
                }

                if (id is >= 0 and <= 2 or -1 && pixels != null)
                {
                    for (var pixel = 0; pixel < expected; pixel++)
                    {
                        pixels[pixel * 4 + (id == -1 ? 3 : id)] = plane[pixel];
                    }
                }
                stream.Position = end;
            }
            if (pixels != null && record.Opacity != 255)
            {
                for (var pixel = 3; pixel < pixels.Length; pixel += 4)
                {
                    pixels[pixel] = (byte)((pixels[pixel] * record.Opacity + 127) / 255);
                }
            }
            layers.Add((record, pixels == null ? null : new RgbaImage(record.Width, record.Height, pixels)));
        }
        var roots = new List<PsdWriter.Layer>();
        var stack = new Stack<List<PsdWriter.Layer>>();
        stack.Push(roots);
        foreach (var (record, image) in layers.AsEnumerable().Reverse())
        {
            if (record.Divider is 1 or 2)
            {
                var children = new List<PsdWriter.Layer>();
                stack.Peek().Add(new PsdWriter.Layer(record.Name, Children: children));
                stack.Push(children);
            }
            else if (record.Divider == 3)
            {
                if (stack.Count == 1)
                {
                    throw new InvalidDataException("Unbalanced PSD group");
                }

                stack.Pop();
            }
            else if (image != null && (record.Flags & 2) == 0)
            {
                stack.Peek().Add(new PsdWriter.Layer(record.Name, image, record.X, record.Y));
            }
        }
        if (stack.Count != 1)
        {
            throw new InvalidDataException("Unbalanced PSD groups");
        }

        return roots;
    }

    private static RgbaImage ReadComposite(BinaryReader reader, int width, int height, int channels)
    {
        if (channels is < 3 or > 4)
        {
            throw new NotSupportedException("PSD composite requires RGB or RGBA channels");
        }

        var planeSize = checked(width * height);
        var planes = new byte[checked(planeSize * channels)];
        var mode = U16(reader);
        if (mode == 0)
        {
            reader.BaseStream.ReadExactly(planes);
        }
        else if (mode == 1)
        {
            var lengths = Enumerable.Range(0, checked(height * channels)).Select(_ => (int)U16(reader)).ToArray();
            for (var row = 0; row < lengths.Length; row++)
            {
                var packed = reader.ReadBytes(lengths[row]);
                var target = planes.AsSpan(row * width, width);
                var count = 0;
                for (var cursor = 0; cursor < packed.Length;)
                {
                    var control = unchecked((sbyte)packed[cursor++]);
                    if (control == -128)
                    {
                        continue;
                    }

                    var run = control >= 0 ? control + 1 : 1 - control;
                    if (count + run > width || cursor + (control >= 0 ? run : 1) > packed.Length)
                    {
                        throw new InvalidDataException("Invalid PSD composite RLE");
                    }

                    if (control >= 0)
                    {
                        packed.AsSpan(cursor, run).CopyTo(target[count..]);
                        cursor += run;
                    }
                    else
                    {
                        target.Slice(count, run).Fill(packed[cursor++]);
                    }

                    count += run;
                }
                if (count != width)
                {
                    throw new InvalidDataException("Invalid PSD composite row width");
                }
            }
        }
        else if (mode is 2 or 3)
        {
            using var zlib = new ZLibStream(reader.BaseStream, CompressionMode.Decompress, true);
            zlib.ReadExactly(planes);
            if (zlib.ReadByte() != -1)
            {
                throw new InvalidDataException("PSD composite exceeds dimensions");
            }

            if (mode == 3)
            {
                for (var row = 0; row < height * channels; row++)
                {
                    for (var column = 1; column < width; column++)
                    {
                        var offset = row * width + column;
                        planes[offset] = unchecked((byte)(planes[offset] + planes[offset - 1]));
                    }
                }
            }
        }
        else
        {
            throw new NotSupportedException("Unsupported PSD composite compression: " + mode);
        }

        var pixels = new byte[checked(planeSize * 4)];
        for (var pixel = 0; pixel < planeSize; pixel++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                pixels[pixel * 4 + channel] = planes[channel * planeSize + pixel];
            }

            pixels[pixel * 4 + 3] = channels == 4 ? planes[3 * planeSize + pixel] : (byte)255;
        }
        return new RgbaImage(width, height, pixels);
    }

    private static ushort U16(BinaryReader reader)
    {
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));
    }

    private static int I32(BinaryReader reader)
    {
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(reader.ReadBytes(4));
    }

    private static int U32(BinaryReader reader)
    {
        return checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4)));
    }

    private static void Skip(BinaryReader reader, int count)
    {
        if (count < 0 || reader.BaseStream.Position + count > reader.BaseStream.Length)
        {
            throw new EndOfStreamException();
        }

        reader.BaseStream.Position += count;
    }
}
