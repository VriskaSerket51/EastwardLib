using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SimpleJSON;

namespace EastwardLib.Assets;

public class AtlasAsset : PackageAsset
{
    private readonly Dictionary<string, byte[]> _images;

    public AtlasAsset(Dictionary<string, Asset> assets) : base(assets)
    {
        var atlas = (TextAsset)assets["atlas.json"];
        assets.Remove("atlas.json");

        JSONNode atlasNode = JSONNode.Parse(atlas.Text);
        var atlasesNode = atlasNode["atlases"].AsArray;
        Dictionary<int, Image> atlases = new Dictionary<int, Image>(atlasesNode.Count);
        for (var i = 0; i < atlasesNode.Count; i++)
        {
            var value = atlasesNode[i];
            string name = value["name"].Value;
            var hmg = (HmgAsset)Children[name];
            using MemoryStream ms = new MemoryStream();
            hmg.SaveTo(ms);
            atlases.Add(i, Image.FromStream(ms));
        }

        var itemsNode = atlasNode["items"].AsArray;
        _images = new(itemsNode.Count);
        foreach (var (_, value) in itemsNode)
        {
            string name = value["name"].Value.Replace("\\/", "/");

            int atlasIdx = value["atlas"].AsInt;
            var rectNode = value["rect"];
            Rectangle rect = new Rectangle(rectNode[0].AsInt, rectNode[1].AsInt, rectNode[2].AsInt, rectNode[3].AsInt);
            using var bitmap = new Bitmap(rect.Width, rect.Height);
            using var g = Graphics.FromImage(bitmap);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(atlases[atlasIdx], rect with { X = 0, Y = 0 }, rect, GraphicsUnit.Pixel);
            using MemoryStream ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            _images.Add(name, ms.ToArray());
        }
    }

    public byte[] this[string name] => _images[name];
}