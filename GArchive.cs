using EastwardLib.Assets;
using EastwardLib.MetaData;
using ZstdSharp;

namespace EastwardLib;

public class GArchive : Dictionary<string, byte[]>, IDisposable
{
    private const int MagicHeader = 27191;

    public string ArchiveName { get; }

    private GArchive(string name, int capacity = 100) : base(capacity)
    {
        ArchiveName = name;
    }

    public static GArchive Read(string path)
    {
        string archiveName = Path.GetFileNameWithoutExtension(path);
        return Read(archiveName, File.OpenRead(path));
    }

    public static GArchive Read(string archiveName, byte[] data)
    {
        return Read(archiveName, new MemoryStream(data));
    }

    public static GArchive Read(string archiveName, Stream s)
    {
        using BinaryReader br = new BinaryReader(s);
        if (br.ReadInt32() != MagicHeader) // Yeah Magic Number
        {
            throw new Exception($"{archiveName} is not GArchive!!!");
        }

        int length = br.ReadInt32();
        GArchive g = new GArchive(archiveName, length);
        for (int i = 0; i < length; i++)
        {
            string name = br.ReadNullTerminatedString();
            int offset = br.ReadInt32();
            bool isCompressed = br.ReadInt32() == 2;
            int decompressedSize = br.ReadInt32();
            int compressedSize = br.ReadInt32();

            long position = br.BaseStream.Position;
            br.BaseStream.Position = offset;
            byte[] data = br.ReadBytes(compressedSize).Take(Compressor.GetCompressBound(decompressedSize)).ToArray();

            if (isCompressed)
            {
                using var decompressor = new Decompressor();
                data = decompressor.Unwrap(data).ToArray();
            }

            br.BaseStream.Position = position;

            string fullName = $"{archiveName}/{name}";
            g.Add(fullName, data);
        }

        s.Dispose();

        return g;
    }

    private int CalculateOffset()
    {
        int result = 0;
        result += 4; // Magic Header
        result += 4; // Length
        foreach (var name in Keys)
        {
            result += name.Length + 1; // Including NULL Byte
            result += 4; // Offset
            result += 4; // Is Compressed?
            result += 4; // Decompressed Size
            result += 4; // Compressed Size
        }

        return result;
    }

    public void Write(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory == null)
        {
            throw new Exception();
        }

        Directory.CreateDirectory(directory);

        using var fs = File.OpenWrite(path);
        Write(fs);
    }

    public void Write(Stream s)
    {
        using BinaryWriter bw = new BinaryWriter(s);
        bw.Write(MagicHeader);
        bw.Write(Count);
        int baseOffset = CalculateOffset();
        foreach (var (name, data) in this)
        {
            bw.WriteNullTerminatedString(name.Substring(ArchiveName.Length + 1));
            bw.Write(baseOffset);
            bw.Write(2);
            Compressor compressor = new Compressor();
            var compressed = compressor.Wrap(data);
            bw.Write(data.Length);
            bw.Write(compressed.Length);
            long offset = bw.BaseStream.Position;
            bw.BaseStream.Position = baseOffset;
            bw.Write(compressed);
            baseOffset += compressed.Length;
            bw.BaseStream.Position = offset;
        }
    }

    public void ExtractTo(string path)
    {
        foreach (var (fullName, data) in this)
        {
            var outputPath = Path.Combine(path, fullName);
            var outputDir = Path.GetDirectoryName(outputPath);
            if (outputDir != null && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            File.WriteAllBytes(outputPath, data);
        }
    }

    public void Dispose()
    {
        Clear();
    }
}