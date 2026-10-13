using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace EastwardLib.Utils;

/// <summary>BIFF8 value-only workbooks in a CFB container, for Gii's original XLS importer.</summary>
public static class BiffWorkbook
{
    public sealed record Sheet(string Name, IReadOnlyList<IReadOnlyList<JsonNode?>> Rows);

    public static byte[] Write(IReadOnlyList<Sheet> sheets)
    {
        if (sheets.Count == 0 || sheets.Select(sheet => sheet.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sheets.Count)
        {
            throw new InvalidDataException("Invalid XLS sheets");
        }

        var strings = new Dictionary<string, int>(StringComparer.Ordinal);
        var references = 0;
        var bodies = new List<byte[]>();
        foreach (var sheet in sheets)
        {
            if (sheet.Name.Length is 0 or > 31 || sheet.Name.IndexOfAny([':', '\\', '*', '?', '/', '[', ']', '\0']) >= 0 || sheet.Name.StartsWith('\'') || sheet.Name.EndsWith('\''))
            {
                throw new InvalidDataException("Invalid XLS sheet name: " + sheet.Name);
            }

            if (sheet.Rows.Count > 65536 || sheet.Rows.Any(row => row.Count > 256))
            {
                throw new InvalidDataException("XLS sheet exceeds BIFF8 row or column limits");
            }

            using var body = new MemoryStream();
            Bof(body, 0x10);
            Record(body, 0x81, [0, 0]); // Worksheet, not a dialog sheet.
            var dimensions = new byte[14];
            U32(dimensions, 4, sheet.Rows.Count);
            U16(dimensions, 10, sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(row => row.Count));
            Record(body, 0x200, dimensions);
            for (var row = 0; row < sheet.Rows.Count; row++)
            {
                for (var column = 0; column < sheet.Rows[row].Count; column++)
                {
                    var value = sheet.Rows[row][column];
                    if (value == null)
                    {
                        continue;
                    }

                    var cell = new byte[14];
                    U16(cell, 0, row);
                    U16(cell, 2, column);
                    if (value is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean))
                    {
                        cell[6] = boolean ? (byte)1 : (byte)0;
                        Record(body, 0x205, cell.AsSpan(0, 8));
                    }
                    else if (value is JsonValue number && number.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                    {
                        var numeric = double.Parse(number.ToString(), CultureInfo.InvariantCulture);
                        BinaryPrimitives.WriteDoubleLittleEndian(cell.AsSpan(6), numeric);
                        Record(body, 0x203, cell);
                    }
                    else
                    {
                        var text = value is JsonValue ? value.ToString() : value.ToJsonString();
                        if (text.Length > 32767)
                        {
                            throw new InvalidDataException("XLS cell string exceeds BIFF8 limit");
                        }

                        if (!strings.TryGetValue(text, out var index))
                        {
                            index = strings.Count;
                            strings.Add(text, index);
                        }
                        references++;
                        U32(cell, 6, index);
                        Record(body, 0xfd, cell.AsSpan(0, 10));
                    }
                }
            }
            Record(body, 0x0a, []);
            bodies.Add(body.ToArray());
        }
        using var globals = new MemoryStream();
        Bof(globals, 5);
        Record(globals, 0x42, [0xb0, 4]); // UTF-16 code page.
        Record(globals, 0xe0, new byte[20]); // Default XF referenced by value cells.
        var pointers = new List<long>();
        foreach (var sheet in sheets)
        {
            var data = new byte[8 + sheet.Name.Length * 2];
            data[6] = (byte)sheet.Name.Length;
            data[7] = 1;
            Encoding.Unicode.GetBytes(sheet.Name).CopyTo(data, 8);
            pointers.Add(globals.Position + 4);
            Record(globals, 0x85, data);
        }
        WriteStrings(globals, strings.Keys, references);
        Record(globals, 0x0a, []);
        var globalBytes = globals.ToArray();
        var offset = globalBytes.Length;
        for (var i = 0; i < pointers.Count; i++)
        {
            U32(globalBytes, (int)pointers[i], offset);
            offset += bodies[i].Length;
        }
        using var workbook = new MemoryStream();
        workbook.Write(globalBytes);
        foreach (var body in bodies)
        {
            workbook.Write(body);
        }

        return Container(workbook.ToArray());
    }

    private static void WriteStrings(Stream stream, IEnumerable<string> strings, int references)
    {
        var values = strings.ToArray();
        using var body = new MemoryStream();
        var header = new byte[8];
        U32(header, 0, references);
        U32(header, 4, values.Length);
        body.Write(header);
        var record = 0xfc;
        void Flush()
        {
            Record(stream, record, body.ToArray());
            record = 0x3c;
            body.SetLength(0);
            body.Position = 0;
        }
        foreach (var text in values)
        {
            if (body.Length + 3 > 8224)
            {
                Flush();
            }

            var length = new byte[3];
            U16(length, 0, text.Length);
            length[2] = 1;
            body.Write(length);
            var bytes = Encoding.Unicode.GetBytes(text);
            var position = 0;
            while (position < bytes.Length)
            {
                var count = Math.Min(bytes.Length - position, ((8224 - (int)body.Length) / 2) * 2);
                if (count == 0)
                {
                    Flush();
                    body.WriteByte(1);
                    continue;
                }
                body.Write(bytes, position, count);
                position += count;
            }
        }
        Flush();
    }

    private static byte[] Container(byte[] workbook)
    {
        const int free = -1, end = -2, fatSector = -3;
        var size = Math.Max(4096, workbook.Length);
        var dataSectors = (size + 511) / 512;
        var fatCount = 1;
        while (fatCount * 128 < dataSectors + 1 + fatCount)
        {
            fatCount++;
        }

        if (fatCount > 109)
        {
            throw new InvalidDataException("XLS workbook exceeds the supported CFB container size");
        }

        var result = new byte[checked((dataSectors + fatCount + 2) * 512)];
        new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }.CopyTo(result, 0);
        U16(result, 24, 0x3e);
        U16(result, 26, 3);
        U16(result, 28, 0xfffe);
        U16(result, 30, 9);
        U16(result, 32, 6);
        U32(result, 44, fatCount);
        U32(result, 48, dataSectors);
        U32(result, 56, 4096);
        U32(result, 60, end);
        U32(result, 68, end);
        for (var i = 0; i < 109; i++)
        {
            U32(result, 76 + i * 4, i < fatCount ? dataSectors + 1 + i : free);
        }

        workbook.CopyTo(result, 512);
        var directory = (dataSectors + 1) * 512;
        void Entry(int index, string name, int type, int start, int length, int child)
        {
            var position = directory + index * 128;
            var encoded = Encoding.Unicode.GetBytes(name + "\0");
            encoded.CopyTo(result, position);
            U16(result, position + 64, encoded.Length);
            result[position + 66] = (byte)type;
            result[position + 67] = 1;
            U32(result, position + 68, free);
            U32(result, position + 72, free);
            U32(result, position + 76, child);
            U32(result, position + 116, start);
            U32(result, position + 120, length);
        }
        Entry(0, "Root Entry", 5, end, 0, 1);
        Entry(1, "Workbook", 2, 0, size, free);
        var fat = (dataSectors + 2) * 512;
        for (var i = 0; i < fatCount * 128; i++)
        {
            U32(result, fat + i * 4, i < dataSectors ? i + 1 == dataSectors ? end : i + 1 : i == dataSectors ? end : i <= dataSectors + fatCount ? fatSector : free);
        }

        return result;
    }

    private static void Bof(Stream stream, int type)
    {
        var bytes = new byte[16];
        U16(bytes, 0, 0x600);
        U16(bytes, 2, type);
        U16(bytes, 4, 0xdbb);
        U16(bytes, 6, 1997);
        U32(bytes, 8, 0x41);
        U32(bytes, 12, 6);
        Record(stream, 0x809, bytes);
    }

    private static void Record(Stream stream, int type, ReadOnlySpan<byte> bytes)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)type);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], checked((ushort)bytes.Length));
        stream.Write(header);
        stream.Write(bytes);
    }

    private static void U16(byte[] bytes, int offset, int value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)value));
    }
    private static void U32(byte[] bytes, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    }
}
