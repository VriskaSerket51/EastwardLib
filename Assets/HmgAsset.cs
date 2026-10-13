using System.Security.Cryptography;
using EastwardLib.Utils;
using K4os.Compression.LZ4;

namespace EastwardLib.Assets;

public class HmgAsset : Asset
{
    public HmgHeader? Header
    {
        get; private set;
    }

    public RgbaImage? Image
    {
        get; private set;
    }

    public HmgAsset()
    {
    }

    public HmgAsset(RgbaImage image)
    {
        Image = image;
    }

    public static HmgAsset FromImage(string path)
    {
        return new(RgbaImage.Load(path));
    }

    public override byte[] Encode()
    {
        var paddingA = Header?.PaddingA.ToArray() ?? CreatePadding();
        var paddingB = Header?.PaddingB.ToArray() ?? CreatePadding();
        return Encode(paddingA, paddingB);
    }

    public byte[] Encode(ReadOnlySpan<byte> paddingA, ReadOnlySpan<byte> paddingB)
    {
        if (paddingA.Length > byte.MaxValue || paddingB.Length > byte.MaxValue)
        {
            throw new ArgumentException("HMG padding cannot exceed 255 bytes per region");
        }
        var image = Image ?? throw new InvalidOperationException("No HMG pixels loaded");
        var compressed = new byte[LZ4Codec.MaximumOutputSize(image.Pixels.Length)];
        var count = LZ4Codec.Encode(image.Pixels, compressed);
        if (count <= 0)
        {
            throw new InvalidDataException("LZ4 image compression failed");
        }

        var result = new byte[checked(HmgHeader.BaseSize + paddingA.Length + paddingB.Length + count)];
        var header = new HmgHeader(result.Length, count, image.Width, image.Height, 32, 1, 0, paddingA.ToArray(), paddingB.ToArray());
        header.Write(result);
        compressed.AsSpan(0, count).CopyTo(result.AsSpan(header.PayloadOffset));
        Header = header;
        return result;
    }

    public override void Decode(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var data = buffer.ToArray();
        var header = HmgHeader.Read(data);
        if (header.AlternateCopyFlag != 0)
        {
            throw new NotSupportedException("Unsupported HMG alternate-copy flag");
        }

        var pixelCount = (long)header.Width * header.Height;
        if (pixelCount > Array.MaxLength / 4)
        {
            throw new InvalidDataException("HMG pixels exceed supported size");
        }

        var pixels = new byte[(int)pixelCount * 4];
        if (LZ4Codec.Decode(data.AsSpan(header.PayloadOffset, header.CompressedSize), pixels) != pixels.Length)
        {
            throw new InvalidDataException("HMG decoded pixel size mismatch");
        }

        Image = new RgbaImage(header.Width, header.Height, pixels);
        Header = header;
    }

    private static byte[] CreatePadding()
    {
        return RandomNumberGenerator.GetBytes(RandomNumberGenerator.GetInt32(1, 5));
    }

    public void SaveTo(Stream stream)
    {
        (Image ?? throw new InvalidOperationException("No HMG pixels loaded")).SavePng(stream);
    }

    public override void SaveTo(string path)
    {
        (Image ?? throw new InvalidOperationException("No HMG pixels loaded")).SavePng(path);
    }
}
