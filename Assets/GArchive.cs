using System.Text;
using EastwardLib.Utils;
using ZstdSharp;

namespace EastwardLib.Assets;

public class GArchive : Dictionary<string, byte[]>
{
    private const int MagicHeader = 27191;
    private readonly Dictionary<string, bool> _compressed = new(StringComparer.Ordinal);
    public string ArchiveName
    {
        get;
    }

    private GArchive(string name, int capacity = 100) : base(capacity, StringComparer.Ordinal)
    {
        ArchiveName = name;
    }

    public static GArchive Create(string name)
    {
        return new(name);
    }

    public void AddOverlay(GArchive archive)
    {
        if (archive.ArchiveName != ArchiveName)
        {
            throw new ArgumentException("Overlay archive names must match", nameof(archive));
        }

        foreach (var (name, bytes) in archive)
        {
            this[name] = bytes;
            if (archive._compressed.TryGetValue(name, out var mode))
            {
                _compressed[name] = mode;
            }
            else
            {
                _compressed.Remove(name);
            }
        }
    }

    public static GArchive Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(Path.GetFileNameWithoutExtension(path), stream, true);
    }

    public static GArchive Read(string name, byte[] data)
    {
        using var stream = new MemoryStream(data, false);
        return Read(name, stream, true);
    }

    public static GArchive Read(string name, Stream stream, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("Archive input must be seekable", nameof(stream));
        }

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen);
        var start = stream.Position;
        var length = stream.Length - start;
        if (length < 8 || reader.ReadInt32() != MagicHeader)
        {
            throw new InvalidDataException("Invalid GArchive header");
        }

        var count = reader.ReadInt32();
        if (count < 0 || count > (length - 8) / 17)
        {
            throw new InvalidDataException("Invalid archive entry count");
        }

        var archive = new GArchive(name, count);
        var entries = new List<(string Name, int Offset, int Compression, int Size, int StoredSize)>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = reader.ReadZString();
            var offset = reader.ReadInt32();
            var compression = reader.ReadInt32();
            var size = reader.ReadInt32();
            var stored = reader.ReadInt32();
            if (compression is not (0 or 2) || offset < 0 || size < 0 || stored < 0 || (long)offset + stored > length)
            {
                throw new InvalidDataException($"Invalid archive entry: {entry}");
            }

            entries.Add((entry, offset, compression, size, stored));
        }
        var tableEnd = stream.Position - start;
        using var decompressor = new Decompressor();
        foreach (var entry in entries)
        {
            if (entry.Offset < tableEnd)
            {
                throw new InvalidDataException("Archive payload overlaps index");
            }

            stream.Position = start + entry.Offset;
            var bytes = reader.ReadBytes(entry.StoredSize);
            if (bytes.Length != entry.StoredSize)
            {
                throw new EndOfStreamException();
            }

            if (entry.Compression == 2)
            {
                bytes = decompressor.Unwrap(bytes, entry.Size).ToArray();
            }

            if (bytes.Length != entry.Size)
            {
                throw new InvalidDataException($"Archive size mismatch: {entry.Name}");
            }

            var key = name + "/" + entry.Name;
            archive.Add(key, bytes);
            archive._compressed.Add(key, entry.Compression == 2);
        }
        return archive;
    }

    public void Write(string path)
    {
        path = SafePath.PrepareFile(path);
        using var stream = File.Create(path);
        Write(stream, true);
    }

    public void Write(Stream stream, bool leaveOpen = false)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen);
        using var compressor = new Compressor();
        var entries = new List<(string Name, byte[] Bytes, int Size, int Compression)>();
        var offset = 8;
        foreach (var (key, bytes) in this)
        {
            var prefix = ArchiveName + "/";
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Entry belongs to another archive: " + key);
            }

            var name = key[prefix.Length..];
            if (name.Contains('\0'))
            {
                throw new InvalidDataException("Archive names cannot contain NUL");
            }

            offset = checked(offset + Encoding.UTF8.GetByteCount(name) + 17);
            var compressed = !_compressed.TryGetValue(key, out var mode) || mode;
            entries.Add((name, compressed ? compressor.Wrap(bytes).ToArray() : bytes, bytes.Length, compressed ? 2 : 0));
        }
        writer.Write(MagicHeader);
        writer.Write(entries.Count);
        foreach (var entry in entries)
        {
            writer.WriteZString(entry.Name);
            writer.Write(offset);
            writer.Write(entry.Compression);
            writer.Write(entry.Size);
            writer.Write(entry.Bytes.Length);
            offset = checked(offset + entry.Bytes.Length);
        }
        foreach (var entry in entries)
        {
            writer.Write(entry.Bytes);
        }

        if (stream.CanSeek)
        {
            stream.SetLength(stream.Position);
        }
    }

    public void ExtractTo(string path)
    {
        foreach (var (name, bytes) in this)
        {
            var target = SafePath.ResolveWithin(path, name);
            target = SafePath.PrepareFile(target);
            File.WriteAllBytes(target, bytes);
        }
    }
}
