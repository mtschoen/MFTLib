using System.Runtime.InteropServices;

namespace MFTLib.NativeInterop;

/// <summary>The C library's <c>realpath</c>, which resolves every component's links; .NET has no equivalent.</summary>
static partial class PosixPath
{
    /// <summary>The canonical absolute path of <paramref name="path" />, or null with the errno value when it cannot be resolved.</summary>
    internal static string? RealPath(string path, out int error)
    {
        var resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        try
        {
            error = 0;
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            free(resolved);
        }
    }

    [LibraryImport("libc", EntryPoint = "realpath", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr realpath(string path, IntPtr resolvedPath);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void free(IntPtr pointer);
}
