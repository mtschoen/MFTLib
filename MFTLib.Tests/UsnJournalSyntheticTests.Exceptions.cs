using System.Runtime.InteropServices;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class UsnJournalSyntheticTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Journal_ResultAllocationExceptionReturnsNull(bool watch)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("USN exports are Windows-only.");
        }

        using var handle = FakeVolumeHandle.Create();
        NativeTestHooks.NativeSetAllocFailCountdown(-3);
        var pointer = watch
            ? MFTLibNative.NativeWatchUsnJournalBatchCancelable(handle, 500, 0xABCD, handle)
            : MFTLibNative.NativeReadUsnJournal(handle, 500, 0xABCD, 1);
        Assert.AreEqual(IntPtr.Zero, pointer, "No result storage exists when its allocation throws.");
        AssertOrdinaryParseSucceeds();

        UseFakeHandle();
        NativeTestHooks.NativeSetAllocFailCountdown(-3);
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            using var volume = MftVolume.Open('C');
            volume.Journal.ReadUsnJournalBounded(Cursor, 0);
        });
        Assert.Contains("ReadUsnJournal returned null", failure.Message);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Journal_ExceptionReleasesAllocatedBuffersAndReturnsEmptyFailure(bool watch)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("USN exports are Windows-only.");
        }

        using var handle = FakeVolumeHandle.Create();
        NativeTestHooks.NativeSetAllocFailCountdown(-6);
        var pointer = watch
            ? MFTLibNative.NativeWatchUsnJournalBatchCancelable(handle, 500, 0xABCD, handle)
            : MFTLibNative.NativeReadUsnJournal(handle, 500, 0xABCD, 1);
        Assert.AreNotEqual(IntPtr.Zero, pointer);
        try
        {
            var result = Marshal.PtrToStructure<UsnJournalResultNative>(pointer);
            Assert.Contains("Out of memory", result.ErrorMessage);
            Assert.AreEqual(0UL, result.EntryCount);
            Assert.AreEqual(IntPtr.Zero, result.Entries);
            Assert.AreEqual(500L, result.NextUsn);
        }
        finally
        {
            MFTLibNative.NativeFreeUsnJournalResult(pointer);
        }

        AssertOrdinaryParseSucceeds();
    }

    static void AssertOrdinaryParseSucceeds()
    {
        NativeTestHooks.NativeResetTestState();
        NativeTestHooks.NativeSetMaxThreads(2);
        var path = Path.GetTempFileName();
        try
        {
            Assert.IsTrue(MFTLibNative.GenerateSyntheticMftSized(path, 256, 256, 1024));
            using var input = MftDumpInput.Open(path);
            using var result = input.Parse();
            Assert.IsGreaterThan(0UL, result.UsedRecords);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
