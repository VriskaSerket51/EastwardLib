﻿using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using K4os.Compression.LZ4;

namespace EastwardLib.Assets;

public class HmgAsset : Asset
{
    private const string MagicHeader = "PGF";

    private byte[]? _bitmapData;
    private int _width;
    private int _height;

    public HmgAsset()
    {
    }

    public HmgAsset(Image src)
    {
        _width = src.Width;
        _height = src.Height;

        src.RotateFlip(RotateFlipType.RotateNoneFlipY);
        using MemoryStream bmp = new MemoryStream();
        src.Save(bmp, ImageFormat.Bmp);
        using BinaryReader br = new BinaryReader(bmp);
        br.BaseStream.Seek(0, SeekOrigin.Begin);
        br.ReadInt16();
        br.ReadInt32();
        br.ReadInt32();
        int idx = br.ReadInt32();
        br.BaseStream.Seek(idx, SeekOrigin.Begin);
        var dataLength = (int)(br.BaseStream.Length - br.BaseStream.Position);
        _bitmapData = new byte[dataLength];
        for (int i = 0; i < dataLength; i += 4)
        {
            var b = br.ReadByte();
            var g = br.ReadByte();
            var r = br.ReadByte();
            var a = br.ReadByte();
            _bitmapData[i] = r;
            _bitmapData[i + 1] = g;
            _bitmapData[i + 2] = b;
            _bitmapData[i + 3] = a;
        }
    }

    public override byte[] Encode()
    {
        if (_bitmapData == null)
        {
            throw new InvalidOperationException("You should Encode() after Decode()");
        }

        using MemoryStream stream = new MemoryStream();
        using BinaryWriter bw = new BinaryWriter(stream);
        byte[] compressedData = new byte[LZ4Codec.MaximumOutputSize(_bitmapData.Length)];
        int length = LZ4Codec.Encode(_bitmapData, compressedData);
        bw.Write(Encoding.UTF8.GetBytes(MagicHeader));
        bw.Write((byte)2);
        bw.Write(GetFileSize(length));
        bw.Write((ushort)24984);
        bw.Write(length);
        bw.Write(_width);
        bw.Write(_height);
        bw.Write((byte)32);
        bw.Write((byte)1);
        bw.Write((byte)2);
        bw.Write((byte)0);
        bw.Write((ushort)48297);
        bw.Write(compressedData.Take(length).ToArray());
        return stream.ToArray();
    }

    public override void Decode(Stream s)
    {
        using BinaryReader br = new BinaryReader(s);
        if (new string(br.ReadChars(3)) != MagicHeader)
        {
            throw new Exception("This file is not Hmg File!!!");
        }

        int dummyLen = br.ReadByte();
        br.ReadInt32(); // File Size
        br.ReadBytes(dummyLen); // Dummy Chunk

        int compressedSize = br.ReadInt32();
        _width = br.ReadInt32();
        _height = br.ReadInt32();
        br.ReadByte(); // Fixed to 32, may means RGBA8888

        br.ReadByte(); // Fixed to 1, may means TRUE_TYPE
        dummyLen = br.ReadByte();
        br.ReadByte(); // Maybe Compression type, 0 is lz4
        br.ReadBytes(dummyLen); // Another Dummy Chunk

        byte[] compressedData = br.ReadBytes(compressedSize);
        _bitmapData = new byte[_width * _height * 4];
        LZ4Codec.Decode(compressedData, _bitmapData);
    }

    private int GetFileSize(int compressedSize)
    {
        int result = 0;
        result += 3; // Magic Header
        result += 1;
        result += 4; // File Size
        result += 2;
        result += 4; // Compressed Size
        result += 4; // Width
        result += 4; // Height
        result += 1;
        result += 1;
        result += 1;
        result += 1;
        result += 2;
        result += compressedSize;
        return result;
    }

    public void SaveTo(Stream s)
    {
        if (_bitmapData == null)
        {
            return;
        }

        byte[] bmpData = new byte[_bitmapData.Length];
        for (var i = 0; i < _bitmapData.Length; i += 4)
        {
            byte r = _bitmapData[i];
            byte g = _bitmapData[i + 1];
            byte b = _bitmapData[i + 2];
            byte a = _bitmapData[i + 3];
            bmpData[i] = b;
            bmpData[i + 1] = g;
            bmpData[i + 2] = r;
            bmpData[i + 3] = a;
        }

        unsafe
        {
            fixed (byte* ptr = bmpData)
            {
                using Bitmap image = new Bitmap(_width, _height, _width * 4, PixelFormat.Format32bppArgb,
                    new IntPtr(ptr));
                image.Save(s, ImageFormat.Png);
            }
        }
    }

    public override void SaveTo(string path)
    {
        if (_bitmapData == null)
        {
            return;
        }

        PrepareDirectory(path);

        SaveTo(File.OpenWrite(path));
    }
}