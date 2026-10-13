using System.Buffers.Binary;
using System.IO.Compression;
using StbImageSharp;

namespace EastwardLib.Utils;

/// <summary>Top-down RGBA pixels, independent of desktop graphics APIs.</summary>
public sealed class RgbaImage
{
    private static readonly uint[] CrcTable = CreateCrcTable();
    public int Width
    {
        get;
    }

    public int Height
    {
        get;
    }

    public byte[] Pixels
    {
        get;
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || checked((long)width * height * 4) != pixels.Length)
        {
            throw new ArgumentException("Invalid RGBA dimensions or pixel count");
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public static RgbaImage Load(Stream stream)
    {
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return new RgbaImage(image.Width, image.Height, image.Data);
    }

    public static RgbaImage Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public RgbaImage Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || (long)x + width > Width || (long)y + height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Crop exceeds image bounds");
        }

        var pixels = new byte[checked(width * height * 4)];
        for (var row = 0; row < height; row++)
        {
            Pixels.AsSpan(((y + row) * Width + x) * 4, width * 4).CopyTo(pixels.AsSpan(row * width * 4));
        }

        return new RgbaImage(width, height, pixels);
    }

    public RgbaImage ToStraightAlpha()
    {
        var pixels = (byte[])Pixels.Clone();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            for (var channel = 0; channel < 3; channel++)
            {
                pixels[i + channel] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, (pixels[i + channel] * 255 + alpha / 2) / alpha);
            }
        }
        return new RgbaImage(Width, Height, pixels);
    }

    public byte[] ToPng()
    {
        using var stream = new MemoryStream();
        SavePng(stream);
        return stream.ToArray();
    }

    public void SavePng(string path)
    {
        path = SafePath.PrepareFile(path);
        using var stream = File.Create(path);
        SavePng(stream);
    }

    public void SavePng(Stream stream)
    {
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(stream, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
        {
            for (var row = 0; row < Height; row++)
            {
                zlib.WriteByte(0);
                zlib.Write(Pixels.AsSpan(row * Width * 4, Width * 4));
            }
        }
        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(name);
        stream.Write(data);
        uint crc = 0xffffffff;
        foreach (var value in name)
        {
            crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 255];
        }

        foreach (var value in data)
        {
            crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 255];
        }

        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xffffffff);
        stream.Write(number);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }

            table[i] = crc;
        }
        return table;
    }
}
