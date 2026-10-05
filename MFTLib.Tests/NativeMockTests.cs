using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
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
        MFTLibNative._parseMftFromFile = (_, _, _, _, _, _) => IntPtr.Zero;

        Assert.ThrowsException<InvalidOperationException>(() =>
            DirectParse.ParseFile("fake.bin", out _));
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

        MFTLibNative._parseMftFromFile = (_, _, _, _, _, _) => resultPtr;
        MFTLibNative._freeMftResult = _ => Marshal.FreeHGlobal(resultPtr);

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            DirectParse.ParseFile("fake.bin", out _));
        Assert.AreEqual("Volume is not NTFS", ex.Message);
    }

    [TestMethod]
    public void GenerateSyntheticMFT_ReturnsFalse_ThrowsInvalidOperation()
    {
        MFTLibNative._generateSyntheticMftSized = (_, _, _, _) => false;

        Assert.ThrowsException<InvalidOperationException>(() =>
            MftVolume.GenerateSyntheticMFT("fake.bin", 100));
    }

    [TestMethod]
    public void GenerateFixtureMFT_ReturnsFalse_ThrowsInvalidOperationNamingThePath()
    {
        MFTLibNative._generateFixtureMft = _ => false;

        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
            MftVolume.GenerateFixtureMFT("fake-fixture.bin"));

        StringAssert.Contains(exception.Message, "fake-fixture.bin");
    }

    [TestMethod]
    public void StreamRecords_NullReturn_ThrowsInvalidOperation()
    {
        FileUtilities._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
        MFTLibNative._parseMftRecordsWithProgress = (_, _, _, _, _, _) => IntPtr.Zero;

        using var volume = MftVolume.Open("C");
        // ReSharper disable once AccessToDisposedClosure
        Assert.ThrowsException<InvalidOperationException>(() => volume.StreamRecords(null, MatchFlags.None, null, null, CancellationToken.None));
    }

    [TestMethod]
    public void ParseMftRecordsWithProgressDefault_NoOverrideConfigured_CallsRealNativeExport()
    {
        // Leave _parseMftRecordsWithProgress at its native P/Invoke default (guaranteed by
        // TestCleanup running after every test in this class) so the call reaches the real
        // ParseMFTRecordsWithProgress export.
        // INVALID_HANDLE_VALUE (-1) lets the native side fail gracefully with an error
        // result instead of needing a real, elevated volume handle.
        using var handle = new SafeFileHandle(new IntPtr(-1), false);

        var resultPointer = MFTLibNative._parseMftRecordsWithProgress(
            handle, null, MatchFlags.None, 256, IntPtr.Zero, null);

        Assert.AreNotEqual(IntPtr.Zero, resultPointer);
        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
            var errorMessage = result.ErrorMessage;
            var expectedMessage = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? errorMessage!.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                : !string.IsNullOrEmpty(errorMessage);
            Assert.IsTrue(expectedMessage,
                $"Expected an invalid-handle or unsupported-platform error, got: {errorMessage}");
        }
        finally
        {
            MFTLibNative._freeMftResult(resultPointer);
        }
    }
}
