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
///     progress-callback wiring, bounds-check guards in the record and
///     attribute walkers, and allocation-failure seams reached only through
///     the path-resolution merge phase.
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
            client.SafePipeHandle, null, MatchFlags.None, 256, IntPtr.Zero, null);
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
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
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

    // --- mft.parse.cpp line 208: FileReadChunk's bytesRead<=0 branch, reached
    // via a genuine pread_at failure on the FIRST chunk read (not the header
    // read, which is a separate pread_at call preceding the chunk loop). ---

    [TestMethod]
    public void ParseFromFile_PlatformReadFailOnFirstChunk_ReturnsZeroUsedRecordsWithValidGeometry()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 100, 256);

            // Countdown 2: the 1st ShouldFailPlatformRead() check is the header
            // pread_at (0x20 bytes, used for record-size detection) and must
            // succeed; the 2nd is FileReadChunk's first positioned read, which
            // this countdown fails.
            NativeTestHooks.NativeSetFailPlatformRead(2);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256, IntPtr.Zero, null);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(100UL, result.TotalRecords, "Geometry detection (header read) must have succeeded");
                Assert.AreEqual(0UL, result.UsedRecords);
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

    // --- mft.parse.cpp lines 244-245: empty-file fast path. ---

    [TestMethod]
    public void ParseFromFile_EmptyFile_IsRejectedAsInvalidInput()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, []);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256, IntPtr.Zero, null);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(0UL, result.TotalRecords);
                Assert.AreEqual(0UL, result.UsedRecords);
                Assert.AreEqual("The dump file is empty.", result.ErrorMessage);
                Assert.AreEqual(1u, result.InvalidInput);
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

    // --- mft.parse_core.cpp lines 199-200: paths.strings malloc fails after
    // paths.entries already succeeded (one tick past the existing
    // ParseFromFile_PathEntryAllocFail_LeavesPathsEmpty countdown of 10, which
    // targets the paths.entries malloc itself). ---

    [TestMethod]
    public void ParseFromFile_PathStringsAllocFail_LeavesPathEntriesFreed()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 100, 256);

            // 1=result, 2=lookup gate, 3-5=lookup metadata, 6-7=buffers,
            // 8=entries, 9=strings, 10=path entries, 11=path strings.
            NativeTestHooks.NativeSetAllocFailCountdown(11);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.ResolvePaths, 256, IntPtr.Zero, null);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.UsedRecords > 0);
                Assert.AreEqual(IntPtr.Zero, result.PathEntries);
                Assert.AreNotEqual(IntPtr.Zero, result.Entries, "Fallback to unresolved entries must still publish");
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

    // --- mft.parse_core.cpp lines 224-225, 230-232: the paths-merge
    // AppendSlice failure branch. A deep directory chain makes the cumulative
    // resolved-path string length exceed the paths string pool's initial
    // capacity (seeded from the much smaller flat sum of individual file
    // names), forcing a realloc in the single merge call; failing that
    // realloc exercises the merge-abort path. ---

    [TestMethod]
    public void ParseFromFile_PathStringPoolGrowFail_AbortsPathMerge()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            const int totalRecords = 400;
            MftVolume.GenerateSyntheticMFT(path, totalRecords, 512);
            var data = File.ReadAllBytes(path);

            var chain = new List<(ulong RecordNumber, int NameLength)>();
            ulong parentRecord = 5;
            for (var recordNumber = 6; recordNumber < totalRecords && chain.Count < 96; recordNumber++)
            {
                if (!TrySetParentRecord(data, recordNumber, parentRecord, out var nameLength) || nameLength < 8)
                {
                    continue;
                }

                chain.Add(((ulong)recordNumber, nameLength));
                parentRecord = (ulong)recordNumber;
            }

            Assert.IsTrue(chain.Count > 32, "Need a meaningfully deep chain to overflow the paths string pool");
            File.WriteAllBytes(path, data);

            NativeTestHooks.NativeSetMaxThreads(1);
            // Single-threaded, single-chunk (400 < 512 buffer), ResolvePaths ordinal:
            // 1=result, 2=lookup gate, 3-5=lookup metadata, 6-7=buffers,
            // 8=entries, 9=strings, 10=path entries, 11=path strings,
            // 12=path string-pool growth (the initial merge fits).
            NativeTestHooks.NativeSetAllocFailCountdown(12);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.ResolvePaths, 512, IntPtr.Zero, null);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.UsedRecords > 0);
                Assert.AreEqual(IntPtr.Zero, result.PathEntries);
                Assert.AreNotEqual(IntPtr.Zero, result.Entries, "Fallback to unresolved entries must still publish");
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

            var invocations = new List<(MftScanPhase Phase, ulong Scanned, ulong Total)>();
            MFTLibNative.NativeMftProgressCallback callback = (phase, scanned, total, _, _) =>
                invocations.Add((phase, scanned, total));

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative._parseMftRecordsWithProgress(
                fileStream.SafeFileHandle, null, MatchFlags.None, 64, IntPtr.Zero, callback);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.TotalRecords > 0);
                Assert.IsTrue(invocations.Count >= 1, "Expected at least one in-loop progress report");
                Assert.AreEqual(MftScanPhase.Parsing, invocations[0].Phase);
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

            var invocations = new List<(MftScanPhase Phase, ulong Scanned, ulong Total)>();
            MFTLibNative.NativeMftProgressCallback callback = (phase, scanned, total, _, _) =>
                invocations.Add((phase, scanned, total));

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Fail the 3rd Read: 1=boot sector, 2=record 0, 3=VolumeReadChunk's
            // first read, so the chunk loop body never executes.
            NativeTestHooks.NativeSetReadFailCountdown(3);
            var resultPointer = MFTLibNative._parseMftRecordsWithProgress(
                fileStream.SafeFileHandle, null, MatchFlags.None, 64, IntPtr.Zero, callback);
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
