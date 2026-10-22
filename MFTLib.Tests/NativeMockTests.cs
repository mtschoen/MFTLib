using System.Runtime.InteropServices;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
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
        MFTLibNative.Hooks.ResetToDefaults();
        FileUtilities.Hooks.ResetToDefaults();
        NativeTestHooks.NativeResetTestState();
    }

    [TestMethod]
    public void GenerateSyntheticMFT_ReturnsFalse_ThrowsInvalidOperation()
    {
        var path = Path.GetTempFileName();
        try
        {
            NativeTestHooks.NativeSetAllocFailCountdown(1);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                MftVolume.GenerateSyntheticMFT(path, 100));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void GenerateFixtureMFT_ReturnsFalse_ThrowsInvalidOperationNamingThePath()
    {
        var path = Path.GetTempFileName();
        try
        {
            NativeTestHooks.NativeSetAllocFailCountdown(1);
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
                MftFixture.Generate(path));

            Assert.Contains(path, exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ReadRecordBatches_NullReturn_ThrowsInvalidOperation()
    {
        FileUtilities.Hooks._getVolumeHandle = _ => new SafeFileHandle(new IntPtr(1), false);
        MFTLibNative.Hooks._parseMftRecordsWithProgress = (_, _, _, _, _) => IntPtr.Zero;

        using var volume = MftVolume.Open('C');
        // ReSharper disable once AccessToDisposedClosure
        Assert.ThrowsExactly<InvalidOperationException>(() => volume.ReadRecordBatches(4096, null, null, CancellationToken.None).ToList());
    }

    [TestMethod]
    public void ParseMftRecordsWithProgressDefault_NoOverrideConfigured_CallsRealNativeExport()
    {
        // Leave ParseMftRecordsWithProgress at its native P/Invoke default (guaranteed by
        // TestCleanup running after every test in this class) so the call reaches the real
        // ParseMFTRecordsWithProgress export.
        // INVALID_HANDLE_VALUE (-1) lets the native side fail gracefully with an error
        // result instead of needing a real, elevated volume handle.
        using var handle = new SafeFileHandle(new IntPtr(-1), false);

        var resultPointer = MFTLibNative.Hooks._parseMftRecordsWithProgress(
            handle, false, 256, IntPtr.Zero, null);

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
            MFTLibNative.Hooks._freeMftResult(resultPointer);
        }
    }
}
