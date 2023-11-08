using System.Runtime.InteropServices;
using System.Text;

namespace EastwardLib.Fmod;

public class Studio
{
    [DllImport(".\\fmodstudio")]
    private static extern RESULT FMOD_Studio_System_Create(out IntPtr system, uint headerVersion);

    [DllImport(".\\fmodstudio")]
    private static extern RESULT FMOD_Studio_System_GetCoreSystem(IntPtr system, out IntPtr core);

    [DllImport(".\\fmodstudio")]
    private static extern RESULT FMOD_Studio_System_Initialize(IntPtr system, int maxChannels,
        STUDIO_INITFLAGS studioFlags, INITFLAGS flags, IntPtr extraDriverData = 0);

    [DllImport(".\\fmodstudio")]
    private static extern RESULT
        FMOD_Studio_System_LoadBankFile(IntPtr system, byte[] filename, LOAD_BANK_FLAGS flags, out IntPtr bank);

    private IntPtr _handle;
    private Core _core;

    public Studio()
    {
        FMOD_Studio_System_Create(out _handle, 131335);
        FMOD_Studio_System_GetCoreSystem(_handle, out var coreHandle);
        _core = new Core(coreHandle);
        FMOD_Studio_System_Initialize(_handle, 1, 0, 0);
    }

    public Bank LoadBank(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(path);
        }

        FMOD_Studio_System_LoadBankFile(_handle, BinaryUtility.Utf8String2Bytes(path), 0, out var bank);
        return new Bank(bank);
    }
}