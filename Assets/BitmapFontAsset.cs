using System.Globalization;
using System.Text.RegularExpressions;
using EastwardLib.MetaData;
using EastwardLib.Utils;

namespace EastwardLib.Assets;

public sealed class BitmapFontAsset : TextAsset
{
    private readonly Lazy<IReadOnlyDictionary<int, RgbaImage>> _pages;
    private readonly Dictionary<int, Dictionary<string, int>> _glyphs = new();
    private readonly Dictionary<(int, int), int> _kerning = new();
    private readonly int _height;
    public int GlyphCount => _glyphs.Count;
    public BitmapFontAsset(AssetManager manager, AssetInfo info) : base(System.Text.Encoding.UTF8.GetString(manager.RequireFile(info.ObjectFiles["font"])))
    {
        _pages = new Lazy<IReadOnlyDictionary<int, RgbaImage>>(() => info.Dependency!.Where(pair => pair.Key.StartsWith("tex-", StringComparison.Ordinal)).ToDictionary(pair => int.Parse(pair.Key[4..], CultureInfo.InvariantCulture) - 1, pair => ((HmgAsset)manager.LoadAsset(pair.Value!.ToString())!).Image!));
        foreach (var line in Text.Split('\n'))
        {
            var fields = Regex.Matches(line, @"(\w+)=(-?\d+)").ToDictionary(match => match.Groups[1].Value, match => int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
            if (line.StartsWith("char ", StringComparison.Ordinal))
            {
                _glyphs.Add(fields["id"], fields);
            }

            if (line.StartsWith("common ", StringComparison.Ordinal))
            {
                _height = fields.GetValueOrDefault("lineHeight", 32);
            }

            if (line.StartsWith("kerning ", StringComparison.Ordinal))
            {
                _kerning[(fields["first"], fields["second"])] = fields["amount"];
            }
        }
        if (_height <= 0)
        {
            _height = 32;
        }
    }
    public RgbaImage RenderText(string text)
    {
        var characters = text.EnumerateRunes().Select(rune => rune.Value).Take(512).ToArray();
        var runs = new List<(Dictionary<string, int> Glyph, int X)>();
        var x = 0;
        var previous = -1;
        foreach (var character in characters)
        {
            if (!_glyphs.TryGetValue(character, out var glyph))
            {
                continue;
            }

            x += _kerning.GetValueOrDefault((previous, character));
            runs.Add((glyph, x));
            x += glyph["xadvance"];
            previous = character;
        }
        var left = Math.Min(0, runs.Select(run => run.X + run.Glyph["xoffset"]).DefaultIfEmpty(0).Min());
        var top = Math.Min(0, runs.Select(run => run.Glyph["yoffset"]).DefaultIfEmpty(0).Min());
        var right = Math.Max(1, Math.Max(x, runs.Select(run => run.X + run.Glyph["xoffset"] + run.Glyph["width"]).DefaultIfEmpty(1).Max()));
        var bottom = Math.Max(_height, runs.Select(run => run.Glyph["yoffset"] + run.Glyph["height"]).DefaultIfEmpty(_height).Max());
        var output = new RgbaImage(right - left, bottom - top, new byte[checked((right - left) * (bottom - top) * 4)]);
        foreach (var (glyph, start) in runs)
        {
            var page = _pages.Value[glyph["page"]];
            for (var y = 0; y < glyph["height"]; y++)
            {
                for (var column = 0; column < glyph["width"]; column++)
                {
                    var source = ((glyph["y"] + y) * page.Width + glyph["x"] + column) * 4;
                    var target = ((glyph["yoffset"] - top + y) * output.Width + start + glyph["xoffset"] - left + column) * 4;
                    var alpha = page.Pixels[source + 3] / 255.0;
                    var old = output.Pixels[target + 3] / 255.0;
                    var combined = alpha + old * (1 - alpha);
                    if (combined == 0)
                    {
                        continue;
                    }

                    for (var color = 0; color < 3; color++)
                    {
                        output.Pixels[target + color] = (byte)Math.Clamp(Math.Round((page.Pixels[source + color] * alpha + output.Pixels[target + color] * old * (1 - alpha)) / combined), 0, 255);
                    }

                    output.Pixels[target + 3] = (byte)Math.Round(combined * 255);
                }
            }
        }
        return output;
    }
}
