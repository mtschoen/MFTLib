using System.IO.Pipes;
using System.Runtime.InteropServices;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

/// <summary>
///     Additional tests targeting specific native C++ lines that remained
///     uncovered after the initial coverage burn-down: real (non-mocked)
///     progress-callback wiring and the volume export's extension-record
///     and record-size branches.
/// </summary>
[TestClass]
[DoNotParallelize]
public partial class NativeParserCoverageTests
{
    [TestCleanup]
    public void Cleanup()
    {
        NativeTestHooks.NativeResetTestState();
        MFTLibNative.ResetToDefaults();
        FileUtilities.ResetToDefaults();
    }

    // --- mft.parse.cpp line 63: QueryVolumeRecordSize's real DeviceIoControl
    // failure branch. A plain file handle on an NTFS volume actually lets
    // FSCTL_GET_NTFS_VOLUME_DATA pass through successfully (verified
    // empirically), so a genuinely non-filesystem handle is needed instead: a
    // named pipe, whose driver (NPFS) does not implement that FSCTL at all,
    // distinct from the earlier "Volume handle is invalid" short-circuit that
    // only guards INVALID_HANDLE_VALUE. ---

    [TestMethod]
    public void ParseMFTRecordsWithProgress_NonVolumeHandle_RecordSizeQueryFails()
    {
        var pipeName = "mftlib_test_" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        client.Connect(5000);

        var resultPointer = MFTLibNative._parseMftRecordsWithProgress(
            client.SafePipeHandle, false, 256, IntPtr.Zero, null);
        Assert.AreNotEqual(IntPtr.Zero, resultPointer);
        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
            Assert.IsTrue(
                result.ErrorMessage.Contains("record size", StringComparison.OrdinalIgnoreCase),
                $"Expected a record-size query failure, got: {result.ErrorMessage}");
        }
        finally
        {
            MFTLibNative._freeMftResult(resultPointer);
        }
    }

    // --- mft.parse.cpp line 162: MergeExtensionDataRuns' resident
    // AttributeList bounds guard (ValueOffset + ValueLength > RecordLength). ---

    [TestMethod]
    public void ParseMFTRecords_ResidentAttributeListValueOverflow_SkipsExtensionMerge()
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);

            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);

            // Resident AttributeList whose declared ValueLength overflows its own
            // (deliberately too small) RecordLength: ValueOffset(24) + ValueLength(28) = 52 > 32.
            var a2 = a1 + a1Len;
            data[a2] = 0x20; // TypeCode = AttributeList
            data[a2 + 4] = 32; // RecordLength = 32 (too small for the declared value)
            data[a2 + 5] = 0;
            data[a2 + 8] = 0x00; // FormCode = resident
            data[a2 + 0x10] = 28; // ValueLength = 28 (one 28-byte entry)
            data[a2 + 0x11] = 0;
            data[a2 + 0x14] = 0x18; // ValueOffset = 24

            WriteEndMarker(data, a2 + 32);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                // Parse still succeeds; the malformed AttributeList is simply skipped
                // rather than merged, so total records reflect only the primary run.
                Assert.IsTrue(result.TotalRecords > 0);
            }
            finally
            {
                MFTLibNative._freeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // --- mft.parse_core.cpp lines 300-302: in-loop progress callback,
    // reached only via a real (non-mocked) native progress callback wired
    // through ParseMFTRecordsWithProgress with an actual data payload. ---

    [TestMethod]
    public void ParseMFTRecordsWithProgress_RealCallback_ReportsPerChunkProgress()
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);

            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);
            WriteEndMarker(data, a1 + a1Len);

            File.WriteAllBytes(path, data);

            var invocations = new List<(ulong Scanned, ulong Total)>();
            MFTLibNative.NativeMftProgressCallback callback = (scanned, total, _, _) =>
                invocations.Add((scanned, total));

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative._parseMftRecordsWithProgress(
                fileStream.SafeFileHandle, false, 64, IntPtr.Zero, callback);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.TotalRecords > 0);
                Assert.IsTrue(invocations.Count >= 1, "Expected at least one in-loop progress report");
                Assert.IsTrue(
                    invocations[^1].Scanned == result.TotalRecords && invocations[^1].Total == result.TotalRecords,
                    "Last reported chunk must reach the full record count");
            }
            finally
            {
                MFTLibNative._freeMftResult(resultPointer);
            }

            GC.KeepAlive(callback);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // --- mft.parse_core.cpp lines 313-314: the post-loop "final report"
    // branch, reached only when the loop body never runs (first chunk read
    // fails) while totalRecords (computed from the MFT data runs before any
    // read is attempted) is still nonzero. ---

    [TestMethod]
    public void ParseMFTRecordsWithProgress_RealCallback_FirstChunkReadFails_ReportsFinalOnly()
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);

            var a1 = 4096 + 0x38;
            WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);
            WriteEndMarker(data, a1 + 0x48);

            File.WriteAllBytes(path, data);

            var invocations = new List<(ulong Scanned, ulong Total)>();
            MFTLibNative.NativeMftProgressCallback callback = (scanned, total, _, _) =>
                invocations.Add((scanned, total));

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Fail the 3rd Read: 1=boot sector, 2=record 0, 3=VolumeReadChunk's
            // first read, so the chunk loop body never executes.
            NativeTestHooks.NativeSetReadFailCountdown(3);
            var resultPointer = MFTLibNative._parseMftRecordsWithProgress(
                fileStream.SafeFileHandle, false, 64, IntPtr.Zero, callback);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(0UL, result.UsedRecords);
                Assert.AreEqual(1, invocations.Count, "Only the synthetic final report should fire");
                Assert.AreEqual(invocations[0].Total, invocations[0].Scanned);
            }
            finally
            {
                MFTLibNative._freeMftResult(resultPointer);
            }

            GC.KeepAlive(callback);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
