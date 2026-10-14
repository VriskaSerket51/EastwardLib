using EastwardLib.Utils;

namespace EastwardLib.Assets;

public sealed class SpriteSequenceAsset : JsonAsset
{
    public MSpriteAsset Sprite
    {
        get;
    }
    public string AnimationName
    {
        get;
    }

    public SpriteSequenceAsset(MSpriteAsset sprite, string name) : base(sprite.FindAnimation(name))
    {
        Sprite = sprite;
        AnimationName = name;
    }

    public RgbaImage RenderFrame(int frame)
    {
        return Sprite.RenderFrame(AnimationName, frame);
    }
}
