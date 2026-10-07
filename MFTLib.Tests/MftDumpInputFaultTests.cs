using System.Runtime.InteropServices;
using System.Text;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The failures of a dump input that only an injected fault reaches: a read the platform fails,
///     an allocation that fails, an unreadable size, a mismatched native library, and the native
///     exports called with nothing to work on.
/// </summary>
[TestClass]
[DoNotParallelize]
public class MftDumpInputFaultTests
{
    string _directory = null!;
    string _path = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = MftDumpFixture.NewOwnedDirectory();
        _path = MftDumpFixture.WriteFile(_directory, MftDumpFixture.Standard());
    }

    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
        MFTLibNative.ResetToDefaults();
        MftDumpFixture.DeleteOwnedDirectory(_directory);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(4)]
    public void Parse_AChunkReadFails_FailsAsIncompleteInsteadOfEndingEarly(int failingRead)
    {
        using var input = MftDumpInput.Open(_path);
        NativeTestHooks.NativeSetReadFailCountdown(failingRead);

        var failure = MftDumpInputTests.ParseThrows<InvalidDataException>(input,
            new MftFileScanOptions(BufferSizeRecords: 4));

        Assert.AreEqual(MftDumpInputTests.IncompleteMessage, failure.Message);
    }

    [TestMethod]
    public void Parse_AfterAFailedParse_ReadsTheWholeFileAgain()
    {
        using var input = MftDumpInput.Open(_path);
        NativeTestHooks.NativeSetReadFailCountdown(2);
        MftDumpInputTests.ParseThrows<InvalidDataException>(input, new MftFileScanOptions(BufferSizeRecords: 4));

        using var result = input.Parse(new MftFileScanOptions(BufferSizeRecords: 4));

        Assert.AreEqual(7UL, result.UsedRecords);
    }

    [TestMethod]
    public void Open_HeaderReadFailsOnThePlatform_IsRejectedAsIncomplete()
    {
        if (WindowsOnlyNative.SkipWithoutPlatformReadHook())
        {
            return;
        }

        NativeTestHooks.NativeSetFailPlatformRead(1);

        var failure = Assert.ThrowsException<InvalidDataException>(() => MftDumpInput.Open(_path));

        Assert.AreEqual(MftDumpInputTests.IncompleteMessage, failure.Message);
    }

    [TestMethod]
    public void Parse_ChunkReadFailsOnThePlatform_FailsAsIncomplete()
    {
        if (WindowsOnlyNative.SkipWithoutPlatformReadHook())
        {
            return;
        }

        using var input = MftDumpInput.Open(_path);
        NativeTestHooks.NativeSetFailPlatformRead(2);

        var failure = MftDumpInputTests.ParseThrows<InvalidDataException>(input,
            new MftFileScanOptions(BufferSizeRecords: 4));

        Assert.AreEqual(MftDumpInputTests.IncompleteMessage, failure.Message);
    }

    [TestMethod]
    public void Open_SizeCannotBeRead_ThrowsIOException()
    {
        NativeTestHooks.NativeSetFailFileSize(1);

        var failure = Assert.ThrowsException<IOException>(() => MftDumpInput.Open(_path));

        Assert.AreEqual("Failed to get file size", failure.Message);
    }

    [TestMethod]
    public void Open_InputAllocationFails_ThrowsIOExceptionAndReleasesTheFile()
    {
        NativeTestHooks.NativeSetAllocFailCountdown(1);

        var failure = Assert.ThrowsException<IOException>(() => MftDumpInput.Open(_path));

        Assert.AreEqual("Failed to allocate dump input", failure.Message);
        File.Delete(_path);
        Assert.IsFalse(File.Exists(_path));
    }

    [TestMethod]
    public void Parse_ResultAllocationFails_ThrowsInvalidOperation()
    {
        using var input = MftDumpInput.Open(_path);
        NativeTestHooks.NativeSetAllocFailCountdown(1);

        var failure = MftDumpInputTests.ParseThrows<InvalidOperationException>(input);

        Assert.AreEqual("ParseMftDumpInput returned null", failure.Message);
    }

    [TestMethod]
    public void Open_NativeLibraryOfAnotherAbiVersion_ThrowsBeforeOpeningTheFile()
    {
        MFTLibNative._getMftNativeAbiVersion = () => MFTLibNative.ExpectedMftNativeAbiVersion - 1;

        var failure = Assert.ThrowsException<InvalidOperationException>(
            () => MftDumpInput.Open(Path.Combine(_directory, "absent.mft")));

        StringAssert.Contains(failure.Message, "ABI mismatch");
    }

    [TestMethod]
    public void NativeParse_WithoutAnInput_ReturnsAnErrorResult()
    {
        var resultPointer = NativeTestHooks.NativeParseMftDumpInputRaw(IntPtr.Zero, 4, IntPtr.Zero, IntPtr.Zero,
            IntPtr.Zero);
        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
            Assert.AreEqual("Dump input is invalid", result.ErrorMessage);
            Assert.AreEqual(0u, result.InvalidInput);
            Assert.AreEqual(MFTLibNative.ExpectedMftNativeAbiVersion, result.AbiVersion);
            Assert.AreEqual(MFTLibNative.NativeCompactEntrySize, result.EntryStride);
        }
        finally
        {
            MFTLibNative._freeMftResult(resultPointer);
        }
    }

    [TestMethod]
    public void NativeOpen_WithoutAnInfoBlock_ReturnsNoInput()
    {
        Assert.AreEqual(IntPtr.Zero,
            NativeTestHooks.NativeOpenMftDumpInputRaw(Encoding.UTF8.GetBytes(_path + '\0'), IntPtr.Zero));
    }

    [TestMethod]
    public void NativeClose_WithoutAnInput_DoesNothing()
    {
        MFTLibNative.CloseMftDumpInput(IntPtr.Zero);

        using var input = MftDumpInput.Open(_path);
        Assert.AreEqual(1024u, input.RecordSize);
    }

    [TestMethod]
    public unsafe void NativeParse_ZeroBufferSize_ReadsOneRecordPerChunk()
    {
        var info = Marshal.AllocHGlobal(Marshal.SizeOf<MftDumpInputInfo>());
        var input = NativeTestHooks.NativeOpenMftDumpInputRaw(Encoding.UTF8.GetBytes(_path + '\0'), info);
        try
        {
            Assert.AreNotEqual(IntPtr.Zero, input);
            var opened = Marshal.PtrToStructure<MftDumpInputInfo>(info);
            Assert.AreEqual(16UL * 1024, opened.LengthBytes);
            Assert.AreEqual(1024u, opened.RecordSize);
            Assert.AreEqual(string.Empty, opened.ErrorMessage);

            var resultPointer = NativeTestHooks.NativeParseMftDumpInputRaw(input, 0, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(string.Empty, result.ErrorMessage);
                Assert.AreEqual(7UL, result.UsedRecords);
                var chunkThreadCounts = stackalloc uint[32];
                Assert.AreEqual(16u, NativeTestHooks.NativeGetChunkThreadCounts(chunkThreadCounts, 32),
                    "a zero buffer is raised to one record, so each of the sixteen records is a chunk");
            }
            finally
            {
                MFTLibNative._freeMftResult(resultPointer);
            }
        }
        finally
        {
            MFTLibNative.CloseMftDumpInput(input);
            Marshal.FreeHGlobal(info);
        }
    }
}
