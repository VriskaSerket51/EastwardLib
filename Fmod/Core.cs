namespace EastwardLib.Fmod;

public class Core
{
    private IntPtr _handle;

    public Core(IntPtr core)
    {
        _handle = core;
    }
}