using System.Runtime.InteropServices;
using MFTLib.NativeInterop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

static partial class NativeTestHooks
{
    // Returns copied metadata only. Entries and EntryStrings are freed addresses:
    // their values may be compared with zero, but must never be dereferenced.
    internal static MftParseResult ParseImage(byte[] image, uint bufferSizeRecords = 256)
    {
        using var file = new TempFile();
        File.WriteAllBytes(file.Path, image);
        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ParseHandle(stream.SafeFileHandle.DangerousGetHandle(), bufferSizeRecords)
            ?? throw new AssertFailedException("The native volume parser returned a null result.");
    }

    // Borrows the handle; owns and frees any allocated result before returning.
    // Null means native result allocation failed. Pointer fields are metadata only.
    internal static MftParseResult? ParseHandle(IntPtr volumeHandle, uint bufferSizeRecords = 256)
    {
        var resultPointer = NativeParseMFTRecordsRaw(volumeHandle, bufferSizeRecords);
        if (resultPointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStructure<MftParseResult>(resultPointer);
        }
        finally
        {
            MFTLibNative.Hooks._freeMftResult(resultPointer);
        }
    }
}
