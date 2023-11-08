using System.Runtime.InteropServices;
using System.Text;

namespace EastwardLib.Fmod;

public static class BinaryUtility
{
    public static byte[] Utf8String2Bytes(string s)
    {
        int maximumLength = Encoding.UTF8.GetMaxByteCount(s.Length) + 1;
        byte[] buffer = new byte[maximumLength];

        int byteCount = Encoding.UTF8.GetBytes(s, 0, s.Length, buffer, 0);
        buffer[byteCount] = 0;

        return buffer;
    }

    public static string Pointer2Utf8String(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return string.Empty;
        }

        int handleLength = 0;
        while (Marshal.ReadByte(handle, handleLength) != 0)
        {
            handleLength++;
        }

        if (handleLength == 0)
        {
            return string.Empty;
        }

        byte[] buffer = new byte[handleLength];

        Marshal.Copy(handle, buffer, 0, handleLength);

        int maximumLength = Encoding.UTF8.GetMaxCharCount(handleLength);
        char[] outputBuffer = new char[maximumLength];

        int charCount = Encoding.UTF8.GetChars(buffer, 0, handleLength, outputBuffer, 0);

        return new String(outputBuffer, 0, charCount);
    }
}