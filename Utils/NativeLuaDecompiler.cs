using System.Runtime.InteropServices;
using System.Text;

namespace EastwardLib.Utils;

/// <summary>Load the optional native decompiler on the first source request.</summary>
public sealed class NativeLuaDecompiler : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DecompileFunction(IntPtr bytes, nuint size, out IntPtr output, out nuint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFunction(IntPtr output);

    private sealed record Binding(IntPtr Handle, DecompileFunction Decompile, FreeFunction Free);
    private readonly Lazy<Binding> _binding;
    private readonly object _lock = new();
    private bool _disposed;

    public bool IsLoaded => _binding.IsValueCreated;

    public NativeLuaDecompiler(string? libraryPath = null)
    {
        var path = libraryPath == null ? null : Path.GetFullPath(libraryPath);
        _binding = new Lazy<Binding>(() => Load(path));
    }

    public string Decompile(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var binding = _binding.Value;
            var input = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            var output = IntPtr.Zero;
            try
            {
                var status = binding.Decompile(input.AddrOfPinnedObject(), (nuint)bytes.Length, out output, out var length);
                if (output == IntPtr.Zero)
                {
                    throw new InvalidDataException("Native LuaJIT decompiler could not allocate a result");
                }

                if (length > int.MaxValue)
                {
                    throw new InvalidDataException("Native LuaJIT output exceeds supported size");
                }

                var data = new byte[(int)length];
                Marshal.Copy(output, data, 0, data.Length);
                var source = Encoding.UTF8.GetString(data);
                if (status != 0)
                {
                    throw new InvalidDataException(source);
                }

                return source;
            }
            finally
            {
                if (output != IntPtr.Zero)
                {
                    binding.Free(output);
                }

                input.Free();
            }
        }
    }

    private static Binding Load(string? path)
    {
        var handle = path == null
            ? NativeLibrary.Load("eastward-luajit", typeof(NativeLuaDecompiler).Assembly, DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)
            : NativeLibrary.Load(path);
        try
        {
            return new Binding(handle,
                Marshal.GetDelegateForFunctionPointer<DecompileFunction>(NativeLibrary.GetExport(handle, "ew_decompile")),
                Marshal.GetDelegateForFunctionPointer<FreeFunction>(NativeLibrary.GetExport(handle, "ew_free")));
        }
        catch
        {
            NativeLibrary.Free(handle);
            throw;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_binding.IsValueCreated)
            {
                NativeLibrary.Free(_binding.Value.Handle);
            }

            _disposed = true;
        }
    }
}
