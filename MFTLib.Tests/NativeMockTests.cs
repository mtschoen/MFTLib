using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib.Interop;

namespace MFTLib.Tests;

[TestClass]
public class NativeMockTests
{
    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
    }

    [TestMethod]
    public void ParseMFTFromFile_NullReturn_ThrowsInvalidOperation()
    {
        MFTLibNative.ParseMFTFromFile = (_, _, _, _) => IntPtr.Zero;

        Assert.ThrowsException<InvalidOperationException>(() =>
            MftVolume.ParseMFTFromFile("fake.bin", out _));
    }

    [TestMethod]
    public void ParseMFTFromFile_NativeErrorMessage_ThrowsWithMessage()
    {
        var errorResult = new MftParseResult { ErrorMessage = "Volume is not NTFS" };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(errorResult, resultPtr, false);

        MFTLibNative.ParseMFTFromFile = (_, _, _, _) => resultPtr;
        MFTLibNative.FreeMftResult = _ => Marshal.FreeHGlobal(resultPtr);

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            MftVolume.ParseMFTFromFile("fake.bin", out _));
        Assert.AreEqual("Volume is not NTFS", ex.Message);
    }

    [TestMethod]
    public void GenerateSyntheticMFT_ReturnsFalse_ThrowsInvalidOperation()
    {
        MFTLibNative.GenerateSyntheticMFT = (_, _, _) => false;

        Assert.ThrowsException<InvalidOperationException>(() =>
            MftVolume.GenerateSyntheticMFT("fake.bin", 100));
    }

    [TestMethod]
    public void StreamRecords_NullReturn_ThrowsInvalidOperation()
    {
        FileUtilities.GetVolumeHandle = _ => new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(1), ownsHandle: false);
        MFTLibNative.ParseMFTRecords = (_, _, _, _) => IntPtr.Zero;

        using var volume = MftVolume.Open("C");
        // ReSharper disable once AccessToDisposedClosure
        Assert.ThrowsException<InvalidOperationException>(() => volume.StreamRecords());
    }

    [TestMethod]
    public unsafe void FindRecords_NullFullPath_FallsBackToFileName()
    {
        FileUtilities.GetVolumeHandle = _ => new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(1), ownsHandle: false);

        // Build a synthetic MftParseResult with one entry (no path data)
        var entrySize = MftResult.NativeEntrySize;
        var entryBuf = stackalloc byte[entrySize];
        new Span<byte>(entryBuf, entrySize).Clear();

        // recordNumber = 100, parentRecordNumber = 5, flags = 1 (InUse)
        *(ulong*)entryBuf = 100;
        *(ulong*)(entryBuf + 8) = 5;
        *(ushort*)(entryBuf + 16) = 1;

        // nameLength = 4 chars, name = "test"
        *(ushort*)(entryBuf + 18) = 4;
        var nameSpan = new Span<char>(entryBuf + MftResult.NativeStringOffset, 4);
        "test".AsSpan().CopyTo(nameSpan);

        // Build MftParseResult with PathEntries = IntPtr.Zero (no paths)
        var result = new MftParseResult
        {
            TotalRecords = 1,
            UsedRecords = 1,
            Entries = (IntPtr)entryBuf,
            PathEntries = IntPtr.Zero,
        };

        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);

        MFTLibNative.ParseMFTRecords = (_, _, _, _) => resultPtr;
        MFTLibNative.FreeMftResult = _ => Marshal.FreeHGlobal(resultPtr);

        using var volume = MftVolume.Open("C");
        var paths = volume.FindRecords("test").ToList();

        Assert.IsTrue(paths.Count > 0);
        Assert.AreEqual("test", paths[0]); // Fell back to FileName since FullPath is null
    }
}
