using System.Buffers.Binary;
using System.Text;

namespace EastwardLib.Utils;

/// <summary>Write editable RGB layers without a native graphics dependency.</summary>
public static class PsdWriter
{
    public sealed record Layer(string Name, RgbaImage? Image = null, int X = 0, int Y = 0, IReadOnlyList<Layer>? Children = null);

    public static byte[] Write(RgbaImage image)
    {
        if (image.Width > 30000 || image.Height > 30000)
        {
            throw new InvalidDataException("Unsupported PSD document size");
        }

        using var output = new MemoryStream();
        output.Write("8BPS"u8);
        U16(output, 1);
        output.Write(new byte[6]);
        U16(output, 4);
        U32(output, image.Height);
        U32(output, image.Width);
        U16(output, 8);
        U16(output, 3);
        output.Write(new byte[14]);
        var count = image.Width * image.Height;
        var plane = new byte[count];
        for (var channel = 0; channel < 4; channel++)
        {
            for (var pixel = 0; pixel < count; pixel++)
            {
                plane[pixel] = image.Pixels[pixel * 4 + channel];
            }

            output.Write(plane);
        }
        return output.ToArray();
    }

    public static byte[] Write(IReadOnlyList<Layer> layers)
    {
        var entries = new List<(Layer Layer, int Divider)>();
        var width = 1;
        var height = 1;
        void Collect(IReadOnlyList<Layer> items)
        {
            foreach (var layer in items)
            {
                if (layer.Children != null)
                {
                    entries.Add((layer, 1));
                    Collect(layer.Children);
                    entries.Add((new Layer("</Layer group>"), 3));
                }
                else
                {
                    var image = layer.Image ?? throw new InvalidDataException("Missing PSD layer pixels");
                    width = Math.Max(width, checked(layer.X + image.Width));
                    height = Math.Max(height, checked(layer.Y + image.Height));
                    entries.Add((layer, 0));
                }
            }
        }
        Collect(layers);
        if (entries.Count is 0 or > 32767 || width > 30000 || height > 30000)
        {
            throw new InvalidDataException("Unsupported PSD document size");
        }

        using var records = new MemoryStream();
        using var channels = new MemoryStream();
        U16(records, entries.Count);
        foreach (var (layer, divider) in entries.AsEnumerable().Reverse())
        {
            var image = layer.Image;
            U32(records, image == null ? 0 : layer.Y);
            U32(records, image == null ? 0 : layer.X);
            U32(records, image == null ? 0 : checked(layer.Y + image.Height));
            U32(records, image == null ? 0 : checked(layer.X + image.Width));
            U16(records, image == null ? 0 : 4);
            if (image != null)
            {
                var count = image.Pixels.Length / 4;
                for (var c = 0; c < 4; c++)
                {
                    U16(records, c == 3 ? -1 : c);
                    U32(records, count + 2);
                    U16(channels, 0);
                    var plane = new byte[count];
                    for (var p = 0; p < count; p++)
                    {
                        plane[p] = image.Pixels[p * 4 + c];
                    }

                    channels.Write(plane);
                }
            }
            records.Write("8BIMnorm"u8);
            records.Write(new byte[] { 255, 0, 0, 0 });
            using var extra = new MemoryStream();
            U32(extra, 0);
            U32(extra, 0);
            var name = Encoding.UTF8.GetBytes(layer.Name).AsSpan();
            if (name.Length > 255)
            {
                name = name[..255];
            }

            extra.WriteByte((byte)name.Length);
            extra.Write(name);
            extra.Write(new byte[(4 - (name.Length + 1) % 4) % 4]);
            using var unicode = new MemoryStream();
            U32(unicode, layer.Name.Length);
            unicode.Write(Encoding.BigEndianUnicode.GetBytes(layer.Name));
            Tag(extra, "luni", unicode.ToArray());
            if (divider != 0)
            {
                using var data = new MemoryStream();
                U32(data, divider);
                Tag(extra, "lsct", data.ToArray());
            }
            U32(records, checked((int)extra.Length));
            extra.Position = 0;
            extra.CopyTo(records);
        }
        channels.Position = 0;
        channels.CopyTo(records);
        if (records.Length % 2 != 0)
        {
            records.WriteByte(0);
        }

        using var section = new MemoryStream();
        U32(section, checked((int)records.Length));
        records.Position = 0;
        records.CopyTo(section);
        U32(section, 0);
        using var output = new MemoryStream();
        output.Write("8BPS"u8);
        U16(output, 1);
        output.Write(new byte[6]);
        U16(output, 4);
        U32(output, height);
        U32(output, width);
        U16(output, 8);
        U16(output, 3);
        U32(output, 0);
        U32(output, 0);
        U32(output, checked((int)section.Length));
        section.Position = 0;
        section.CopyTo(output);
        U16(output, 0);
        // Layers are authoritative; retain a transparent merged preview rather than inventing a compositing order.
        output.Write(new byte[checked(width * height * 4)]);
        return output.ToArray();
    }

    private static void Tag(Stream stream, string key, byte[] data)
    {
        stream.Write(Encoding.ASCII.GetBytes("8BIM" + key));
        U32(stream, data.Length);
        stream.Write(data);
        if (data.Length % 2 != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void U16(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, unchecked((ushort)value));
        stream.Write(bytes);
    }

    private static void U32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
