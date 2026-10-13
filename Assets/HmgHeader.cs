using System.Buffers.Binary;

namespace EastwardLib.Assets;

/// <summary>The PGF header; the two padding regions make its length variable.</summary>
public sealed record HmgHeader(
    int FileSize,
    int CompressedSize,
    int Width,
    int Height,
    byte PixelWidthDeclaration,
    byte Acceleration,
    byte AlternateCopyFlag,
    ReadOnlyMemory<byte> PaddingA,
    ReadOnlyMemory<byte> PaddingB)
{
    public const int BaseSize = 24;
    public int PayloadOffset => BaseSize + PaddingA.Length + PaddingB.Length;

    public static HmgHeader Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < BaseSize || !data[..3].SequenceEqual("PGF"u8))
        {
            throw new InvalidDataException("Invalid or truncated HMG header");
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != data.Length)
        {
            throw new InvalidDataException("HMG file size mismatch");
        }
        var fields = 8 + data[3];
        if (fields > data.Length - 16)
        {
            throw new InvalidDataException("Truncated HMG header padding");
        }
        var payload = fields + 16 + data[fields + 14];
        if (payload > data.Length)
        {
            throw new InvalidDataException("Truncated HMG payload padding");
        }
        var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data[fields..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(data[(fields + 4)..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(data[(fields + 8)..]);
        if (compressedSize == 0 || compressedSize != data.Length - payload || width <= 0 || height <= 0)
        {
            throw new InvalidDataException("Invalid HMG payload or dimensions");
        }
        return new HmgHeader(data.Length, (int)compressedSize, width, height,
            data[fields + 12], data[fields + 13], data[fields + 15],
            data.Slice(8, data[3]).ToArray(), data.Slice(fields + 16, data[fields + 14]).ToArray());
    }

    internal void Write(Span<byte> data)
    {
        if (PaddingA.Length > byte.MaxValue || PaddingB.Length > byte.MaxValue || data.Length != FileSize || FileSize != PayloadOffset + CompressedSize)
        {
            throw new InvalidDataException("Invalid HMG header sizes");
        }
        "PGF"u8.CopyTo(data);
        data[3] = (byte)PaddingA.Length;
        BinaryPrimitives.WriteInt32LittleEndian(data[4..], FileSize);
        PaddingA.Span.CopyTo(data[8..]);
        var fields = 8 + PaddingA.Length;
        BinaryPrimitives.WriteInt32LittleEndian(data[fields..], CompressedSize);
        BinaryPrimitives.WriteInt32LittleEndian(data[(fields + 4)..], Width);
        BinaryPrimitives.WriteInt32LittleEndian(data[(fields + 8)..], Height);
        data[fields + 12] = PixelWidthDeclaration;
        data[fields + 13] = Acceleration;
        data[fields + 14] = (byte)PaddingB.Length;
        data[fields + 15] = AlternateCopyFlag;
        PaddingB.Span.CopyTo(data[(fields + 16)..]);
    }
}
