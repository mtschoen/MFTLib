using System.IO.Pipes;
using System.Runtime.InteropServices;
using MFTLib.NativeInterop;
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
        MFTLibNative.Hooks.ResetToDefaults();
        FileUtilities.Hooks.ResetToDefaults();
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

        var resultPointer = MFTLibNative.Hooks.ParseMftRecordsWithProgress(
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
            MFTLibNative.Hooks.FreeMftResult(resultPointer);
        }
    }

    // --- mft.parse.cpp: MergeExtensionDataRuns' resident AttributeList bounds guard and
    // CollectExtensionRecordNumbers' entry walk. A malformed list fails the parse; a list of
    // empty entries (terminal padding) is legal and parses. ---

    [TestMethod]
    [DataRow(32, 28, 28, -1, false, DisplayName = "ValueLength overflows the attribute")]
    [DataRow(56, 32, 8, -1, false, DisplayName = "entry shorter than the entry header")]
    [DataRow(56, 32, 64, -1, false, DisplayName = "entry extends past the list")]
    [DataRow(56, 32, 0, -1, true, DisplayName = "zero-length entry is terminal padding")]
    [DataRow(32, 8, 0, -1, true, DisplayName = "short zero tail is terminal padding")]
    [DataRow(32, 8, 0, 0, false, DisplayName = "short tail with a nonzero byte")]
    [DataRow(56, 32, 0, 0, false, DisplayName = "zero-length header with nonzero fields")]
    [DataRow(88, 64, 0, 32, false, DisplayName = "zero padding followed by a data entry")]
    public void ParseMFTRecords_ResidentAttributeList_FailsOnlyWhenMalformed(
        int attributeRecordLength, int valueLength, int entryRecordLength, int nonzeroByteOffset, bool parses)
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);

            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);

            var a2 = a1 + a1Len;
            data[a2] = 0x20; // TypeCode = AttributeList
            data[a2 + 4] = (byte)attributeRecordLength;
            data[a2 + 8] = 0x00; // FormCode = resident
            data[a2 + 0x10] = (byte)valueLength;
            data[a2 + 0x14] = 0x18; // ValueOffset = 24
            data[a2 + 0x18 + 4] = (byte)entryRecordLength; // first entry's RecordLength

            if (nonzeroByteOffset >= 0)
            {
                data[a2 + 0x18 + nonzeroByteOffset] = 0x80;
            }

            WriteEndMarker(data, a2 + attributeRecordLength);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                if (parses)
                {
                    Assert.IsTrue(string.IsNullOrEmpty(result.ErrorMessage), result.ErrorMessage);
                    Assert.IsTrue(result.TotalRecords > 0);
                }
                else
                {
                    Assert.IsTrue(
                        result.ErrorMessage.Contains("malformed attribute list", StringComparison.Ordinal),
                        $"Expected a malformed attribute list failure, got: {result.ErrorMessage}");
                }
            }
            finally
            {
                MFTLibNative.Hooks.FreeMftResult(resultPointer);
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
            MFTLibNative.NativeMftProgressCallback callback = (scanned, total, _) =>
                invocations.Add((scanned, total));

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative.Hooks.ParseMftRecordsWithProgress(
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
                MFTLibNative.Hooks.FreeMftResult(resultPointer);
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

    // --- A first chunk read that fails (totalRecords is computed from the MFT
    // data runs before any read is attempted, so it is still nonzero) fails the
    // parse and reports no progress: the scan did not reach any record. ---

    [TestMethod]
    public void ParseMFTRecordsWithProgress_RealCallback_FirstChunkReadFails_FailsWithoutProgress()
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
            MFTLibNative.NativeMftProgressCallback callback = (scanned, total, _) =>
                invocations.Add((scanned, total));

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Fail the 3rd Read: 1=boot sector, 2=record 0, 3=VolumeReadChunk's
            // first read, so the chunk loop body never executes.
            NativeTestHooks.NativeSetReadFailCountdown(3);
            var resultPointer = MFTLibNative.Hooks.ParseMftRecordsWithProgress(
                fileStream.SafeFileHandle, false, 64, IntPtr.Zero, callback);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(0UL, result.UsedRecords);
                Assert.IsFalse(string.IsNullOrEmpty(result.ErrorMessage), "a failed first chunk must fail the parse");
                Assert.AreEqual(0, invocations.Count, "a scan that read nothing reports no progress");
            }
            finally
            {
                MFTLibNative.Hooks.FreeMftResult(resultPointer);
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
