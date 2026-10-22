#pragma warning disable MSTEST0045 // Noncooperative timeout required for noncancellable native scenarios to guard against infinite retry hangs
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The POSIX positioned read and write retry an interrupted system call (EINTR) and return anything
///     else as it is: an interruption is retried until the real result, a zero result is the end of the
///     file whatever errno was left behind, and a genuine error is returned on the first call. The native
///     hook replaces the system call's result, so no real signal is needed.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NativeInterruptedIoTests : IDisposable
{
    TempDirectory? _tempDirectory;
    string TemporaryDirectoryPath => _tempDirectory!.Path;
    string _path = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The interrupted system call hook exists only in the POSIX native library.");
        }

        _tempDirectory = new TempDirectory("mftlib-dump", create: false);
        _path = MftDumpFixture.WriteFile(TemporaryDirectoryPath, MftDumpFixture.Standard());
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        NativeTestHooks.NativeResetTestState();
        _tempDirectory?.Dispose();
    }

    [TestMethod]
    [Timeout(30_000)]
    [TestCategory("Smoke")]
    public void Open_HeaderReadInterruptedRepeatedly_RetriesUntilTheRealResult()
    {
        NativeTestHooks.NativeSetInterruptedIo(NativeTestHooks.InterruptedIoMode.InterruptBeforeRealResult, 3);

        using var input = MftDumpInput.Open(_path);

        Assert.AreEqual(1024u, input.RecordSize);
        Assert.AreEqual(4, NativeTestHooks.NativeGetInterruptedIoCalls(), "three interruptions, then the real read");
    }

    [TestMethod]
    [Timeout(30_000)]
    [TestCategory("Smoke")]
    public void Parse_ChunkReadInterruptedRepeatedly_RetriesAndReadsEveryRecord()
    {
        using var input = MftDumpInput.Open(_path);
        NativeTestHooks.NativeSetInterruptedIo(NativeTestHooks.InterruptedIoMode.InterruptBeforeRealResult, 3);

        using var result = input.Parse(new MftFileScanOptions(BufferSizeRecords: 4));

        Assert.AreEqual(16UL, result.TotalRecords);
        Assert.AreEqual(7UL, result.UsedRecords);
        Assert.AreEqual(3 + 4, NativeTestHooks.NativeGetInterruptedIoCalls(),
            "three interruptions, then one read for each of the four chunks");
    }

    [TestMethod]
    [Timeout(30_000)]
    [TestCategory("Smoke")]
    public void Generate_WriteInterruptedRepeatedly_RetriesAndWritesTheSameBytes()
    {
        var expectedPath = Path.Combine(TemporaryDirectoryPath, "expected.mft");
        var interruptedPath = Path.Combine(TemporaryDirectoryPath, "interrupted.mft");
        MftVolume.GenerateSyntheticMFT(expectedPath, 100, 256);
        NativeTestHooks.NativeSetInterruptedIo(NativeTestHooks.InterruptedIoMode.InterruptBeforeRealResult, 3);

        MftVolume.GenerateSyntheticMFT(interruptedPath, 100, 256);

        Assert.IsGreaterThanOrEqualTo(4, NativeTestHooks.NativeGetInterruptedIoCalls());
        Assert.AreSequenceEqual(File.ReadAllBytes(expectedPath), File.ReadAllBytes(interruptedPath));
    }

    [TestMethod]
    [Timeout(30_000)]
    [TestCategory("Smoke")]
    public void Open_ZeroResultWithAStaleInterrupt_IsTheEndOfTheFileNotARetry()
    {
        NativeTestHooks.NativeSetInterruptedIo(NativeTestHooks.InterruptedIoMode.ZeroWithStaleInterrupt, 0);

        var failure = Assert.ThrowsExactly<InvalidDataException>(() => MftDumpInput.Open(_path));

        Assert.AreEqual(MftDumpInputTests.IncompleteMessage, failure.Message);
        Assert.AreEqual(1, NativeTestHooks.NativeGetInterruptedIoCalls(), "a zero result is never retried");
    }

    [TestMethod]
    [Timeout(30_000)]
    [TestCategory("Smoke")]
    public void Open_GenuineReadError_IsReturnedOnTheFirstCall()
    {
        NativeTestHooks.NativeSetInterruptedIo(NativeTestHooks.InterruptedIoMode.GenuineError, 0);

        var failure = Assert.ThrowsExactly<InvalidDataException>(() => MftDumpInput.Open(_path));

        Assert.AreEqual(MftDumpInputTests.IncompleteMessage, failure.Message);
        Assert.AreEqual(1, NativeTestHooks.NativeGetInterruptedIoCalls(), "a genuine error is never retried");
    }

    [TestMethod]
    [Timeout(30_000)]
    [TestCategory("Smoke")]
    public void Generate_GenuineWriteError_FailsOnTheFirstCall()
    {
        var path = Path.Combine(TemporaryDirectoryPath, "failed.mft");
        NativeTestHooks.NativeSetInterruptedIo(NativeTestHooks.InterruptedIoMode.GenuineError, 0);

        Assert.IsFalse(MFTLibNative.GenerateSyntheticMftSized(path, 100, 256, 1024));

        Assert.AreEqual(1, NativeTestHooks.NativeGetInterruptedIoCalls(), "a genuine error is never retried");
    }
}
