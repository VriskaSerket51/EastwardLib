using System.Text;

namespace EastwardLib.Utils;

public static class BinaryExtension
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ReadZString(this BinaryReader reader)
    {
        using var bytes = new MemoryStream();
        byte value;
        while ((value = reader.ReadByte()) != 0)
        {
            bytes.WriteByte(value);
        }

        return StrictUtf8.GetString(bytes.ToArray());
    }

    public static void WriteZString(this BinaryWriter writer, string value)
    {
        if (value.Contains('\0'))
        {
            throw new ArgumentException("NUL in archive name", nameof(value));
        }

        writer.Write(StrictUtf8.GetBytes(value));
        writer.Write((byte)0);
    }

    public static byte[] RemovePadding(this byte[] data)
    {
        var length = data.Length;
        while (length > 0 && data[length - 1] == 0)
        {
            length--;
        }

        return data[..length];
    }
}
