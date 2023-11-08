using System.Runtime.InteropServices;

namespace EastwardLib.Fmod;

public class Bank
{
    [DllImport(".\\fmodstudio")]
    private static extern RESULT FMOD_Studio_Bank_GetStringCount(IntPtr bank, out int count);

    [DllImport(".\\fmodstudio")]
    private static extern RESULT FMOD_Studio_Bank_GetStringInfo(IntPtr bank, int index, out Guid id, IntPtr path,
        int size, out int retrieved);

    private IntPtr _handle;

    public Bank(IntPtr bank)
    {
        _handle = bank;
    }

    public int GetStringCount()
    {
        FMOD_Studio_Bank_GetStringCount(_handle, out int count);
        return count;
    }

    public void GetStringInfo(int index, out Guid id, out string path)
    {
        IntPtr pathHandle = Marshal.AllocHGlobal(256);
        FMOD_Studio_Bank_GetStringInfo(_handle, index, out id, pathHandle, 256, out var retrieved);
        path = BinaryUtility.Pointer2Utf8String(pathHandle);
        Marshal.FreeHGlobal(pathHandle);
    }
}