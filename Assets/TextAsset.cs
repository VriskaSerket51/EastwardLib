using System.Text;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public class TextAsset : Asset
{
    private string? _text;

    public TextAsset()
    {
    }

    public TextAsset(string text)
    {
        _text = text;
    }

    public string Text => _text ?? string.Empty;

    public override byte[] Encode()
    {
        return Encoding.UTF8.GetBytes(_text ?? throw new InvalidOperationException("No text data loaded"));
    }

    public override void Decode(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true);
        _text = reader.ReadToEnd();
    }

    public override void SaveTo(string path)
    {
        path = SafePath.PrepareFile(path);
        File.WriteAllBytes(path, Encode());
    }
}
