using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

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
        MFTLibNative._parseMftFromFile = (_, _, _, _) => IntPtr.Zero;

        Assert.ThrowsException<InvalidOperationException>(() =>
            MftVolume.ParseMFTFromFile("fake.bin", out _));
    }

    [TestMethod]
    public void ParseMFTFromFile_NativeErrorMessage_ThrowsWithMessage()
    {
        var errorResult = new MftParseResult
        {
            ErrorMessage = "Volume is not NTFS",
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };
        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(errorResult, resultPtr, false);

        MFTLibNative._parseMftFromFile = (_, _, _, _) => resultPtr;
        MFTLibNative._freeMftResult = _ => Marshal.FreeHGlobal(resultPtr);

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            MftVolume.ParseMFTFromFile("fake.bin", out _));
        Assert.AreEqual("Volume is not NTFS", ex.Message);
    }

    [TestMethod]
    public void GenerateSyntheticMFT_ReturnsFalse_ThrowsInvalidOperation()
    {
        MFTLibNative._generateSyntheticMft = (_, _, _) => false;

        Assert.ThrowsException<InvalidOperationException>(() =>
            MftVolume.GenerateSyntheticMFT("fake.bin", 100));
    }

    [TestMethod]
    public void StreamRecords_NullReturn_ThrowsInvalidOperation()
    {
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
        MFTLibNative._parseMftRecords = (_, _, _, _) => IntPtr.Zero;

        using var volume = MftVolume.Open("C");
        // ReSharper disable once AccessToDisposedClosure
        Assert.ThrowsException<InvalidOperationException>(() => volume.StreamRecords());
    }

    [TestMethod]
    public unsafe void FindRecords_NullFullPath_FallsBackToFileName()
    {
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);

        // Build a synthetic MftParseResult with one compact entry and string pool
        var entrySize = (int)MFTLibNative.NativeCompactEntrySize;
        var entryBuf = stackalloc byte[entrySize];
        new Span<byte>(entryBuf, entrySize).Clear();

        var stringChars = stackalloc char[4];
        "test".AsSpan().CopyTo(new Span<char>(stringChars, 4));

        // recordNumber = 100, parentRecordNumber = 5, stringOffset = 0, flags = 1 (InUse), stringLength = 4
        *(ulong*)entryBuf = 100;
        *(ulong*)(entryBuf + 8) = 5;
        *(ulong*)(entryBuf + 16) = 0;
        *(uint*)(entryBuf + 24) = 0;
        *(ushort*)(entryBuf + 28) = 1;
        *(ushort*)(entryBuf + 30) = 4;

        // Build MftParseResult with PathEntries = IntPtr.Zero (no paths)
        var result = new MftParseResult
        {
            TotalRecords = 1,
            UsedRecords = 1,
            Entries = (IntPtr)entryBuf,
            EntryStrings = (IntPtr)stringChars,
            EntryStringUnits = 4,
            PathEntries = IntPtr.Zero,
            PathStrings = IntPtr.Zero,
            PathStringUnits = 0,
            AbiVersion = MFTLibNative.ExpectedMftNativeAbiVersion,
            EntryStride = MFTLibNative.NativeCompactEntrySize
        };

        var resultPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftParseResult>());
        Marshal.StructureToPtr(result, resultPtr, false);

        MFTLibNative._parseMftRecords = (_, _, _, _) => resultPtr;
        MFTLibNative._freeMftResult = _ => Marshal.FreeHGlobal(resultPtr);

        using var volume = MftVolume.Open("C");
        var paths = volume.FindRecords("test").ToList();

        Assert.IsTrue(paths.Count > 0);
        Assert.AreEqual("test", paths[0]); // Fell back to FileName since FullPath is null
    }
}
