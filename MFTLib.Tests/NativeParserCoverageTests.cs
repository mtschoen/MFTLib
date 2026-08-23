using System.IO.Pipes;
using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
public class NativeParserCoverageTests
{
    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.NativeResetTestState();
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
            client.SafePipeHandle, null, MatchFlags.None, 256, null, IntPtr.Zero);
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
            var data = BuildSyntheticNtfs();
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
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
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
            MFTLibNative.NativeSetFailPlatformRead(2);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
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
    public void ParseFromFile_EmptyFile_ReturnsEmptyResultWithNoError()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, []);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(0UL, result.TotalRecords);
                Assert.AreEqual(0UL, result.UsedRecords);
                Assert.AreEqual(string.Empty, result.ErrorMessage);
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
    // ParseFromFile_PathEntryAllocFail_LeavesPathsEmpty countdown of 7, which
    // targets the paths.entries malloc itself). ---

    [TestMethod]
    public void ParseFromFile_PathStringsAllocFail_LeavesPathEntriesFreed()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 100, 256);

            // 1=result calloc, 2=lookup.init gate, 3=buf0, 4=buf1, 5=entries,
            // 6=strings, 7=paths.entries malloc (succeeds), 8=paths.strings
            // malloc (fails here).
            MFTLibNative.NativeSetAllocFailCountdown(8);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.ResolvePaths, 256);
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

            MFTLibNative.NativeSetMaxThreads(1);
            // Single-threaded, single-chunk (400 < 512 buffer), ResolvePaths ordinal:
            // 1=result, 2=lookup.init, 3=buf0, 4=buf1, 5=entries, 6=strings,
            // (main-loop merge fits within initial capacity, no extra ticks),
            // 7=paths.entries, 8=paths.strings (initial), 9=paths merge string-pool grow.
            MFTLibNative.NativeSetAllocFailCountdown(9);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.ResolvePaths, 512);
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
            var data = BuildSyntheticNtfs();
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
                fileStream.SafeFileHandle, null, MatchFlags.None, 64, callback, IntPtr.Zero);
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
            var data = BuildSyntheticNtfs();
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
            MFTLibNative.NativeSetReadFailCountdown(3);
            var resultPointer = MFTLibNative._parseMftRecordsWithProgress(
                fileStream.SafeFileHandle, null, MatchFlags.None, 64, callback, IntPtr.Zero);
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

    // --- mft.records.cpp line 73: TryExtractFileName's second bounds check
    // (ValueOffset + requiredNameSize > RecordLength), reached only once the
    // FileNameLength byte itself is corrupted past what the attribute's
    // actual RecordLength can hold (the ValueOffset-only check above it
    // already passes for a normal, unmodified attribute). ---

    [TestMethod]
    public void ParseFromFile_FileNameLengthOverflow_SkipsRecord()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 20, 256);
            var data = File.ReadAllBytes(path);

            // Record 6: StandardInformation ends at 0x38 + 0x60 = 0x98, so FileName
            // starts at 0x98. FileNameLength lives at ValueOffset(0x18) + 64 bytes
            // into the attribute value (matches the FILE_NAME struct layout).
            const int recordOffset = 6 * 1024;
            const int fileNameAttributeOffset = recordOffset + 0x98;
            var valueOffset = data[fileNameAttributeOffset + 0x14];
            var fileNameLengthOffset = fileNameAttributeOffset + valueOffset + 64;
            data[fileNameLengthOffset] = 200; // far more units than RecordLength can hold

            File.WriteAllBytes(path, data);

            var records = MftVolume.ParseMFTFromFile(path, out _);
            Assert.IsTrue(records.Length > 0);
            Assert.IsFalse(records.Any(record => record.RecordNumber == 6));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // --- mft.records.cpp line 89: FindNamedAttribute's FirstAttributeOffset
    // guard (< 42, or overflowing the record). ---

    [TestMethod]
    [DataRow((ushort)10)] // below the minimum of 42
    [DataRow((ushort)1022)] // offset + sizeof(uint32_t) overflows a 1024-byte record
    public void ParseFromFile_InvalidFirstAttributeOffset_SkipsRecord(ushort firstAttributeOffset)
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[1024];
            WriteFileRecord(data, 0);
            data[0x14] = (byte)(firstAttributeOffset & 0xFF);
            data[0x15] = (byte)(firstAttributeOffset >> 8);

            File.WriteAllBytes(path, data);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(1UL, result.TotalRecords);
                Assert.AreEqual(0UL, result.UsedRecords, "The record has no discoverable FileName and must be skipped");
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

    // --- mft.records.cpp line 96: the attribute-iteration bounds check,
    // reached when a well-formed first attribute's RecordLength advances the
    // walk to within 3 bytes of the record boundary (so the *next*
    // iteration's own header-read guard trips). ---

    [TestMethod]
    public void ParseFromFile_AttributeWalkOverflow_SkipsRecord()
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[1024];
            WriteFileRecord(data, 0);

            const int attributeOffset = 0x38; // 56, matches WriteFileRecord's computed FirstAttributeOffset
            // TypeCode = ObjectId (0x40): not StandardInformation/FileName/EndMarker,
            // so it is simply skipped and the walk advances by RecordLength.
            data[attributeOffset] = 0x40;
            data[attributeOffset + 1] = 0;
            data[attributeOffset + 2] = 0;
            data[attributeOffset + 3] = 0;
            // RecordLength = 966: next offset = 56 + 966 = 1022; 1022 + 4 = 1026 > 1024.
            const int recordLength = 966;
            data[attributeOffset + 4] = recordLength & 0xFF;
            data[attributeOffset + 5] = recordLength >> 8;
            data[attributeOffset + 8] = 0; // FormCode = resident

            File.WriteAllBytes(path, data);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(1UL, result.TotalRecords);
                Assert.AreEqual(0UL, result.UsedRecords, "The overflowing attribute walk must skip the record");
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

    // --- mft.records.cpp line 248: EnsureEntryCapacity's realloc-failure
    // branch on a subsequent slice merge during AppendSlice. The first
    // entry-array grow (1024 -> 2048 at ordinal 6) succeeds, and the second
    // grow (2048 -> 4096 at ordinal 7) fails, verifying that partial results
    // are retained alongside the "Failed to grow entry array" error message. ---

    [TestMethod]
    public void ParseFromFile_SecondEntryArrayGrowFail_ReturnsPartialResults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            // Large buffer -> one chunk containing every record; on a multi-core
            // host the per-thread slices are merged sequentially, each exercising
            // AppendSlice's capacity checks.
            MftVolume.GenerateSyntheticMFT(path, 4000, 8192);

            // Countdown 7 fails the second entry-array grow during slice merge
            // (after the initial 1024 -> 2048 realloc at ordinal 6 succeeds),
            // leaving partial records in the result.
            MFTLibNative.NativeSetAllocFailCountdown(7);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 8192);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(
                    errorMessage!.Contains("grow entry array"),
                    $"Expected entry-array growth failure, got: {errorMessage}");
                Assert.IsTrue(result.UsedRecords > 0, "Should have partial results");
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

    // --- Helpers (duplicated from NativeCoverageTests.cs, which keeps them
    // private; kept minimal and specific to what this file's tests need). ---

    static bool TrySetParentRecord(byte[] data, int recordNumber, ulong parentRecord, out int nameLength)
    {
        const int recordSize = 1024;
        var recordOffset = recordNumber * recordSize;
        nameLength = 0;
        if (BitConverter.ToUInt32(data, recordOffset) != 0x454C4946 ||
            (BitConverter.ToUInt16(data, recordOffset + 0x16) & 1) == 0 ||
            (BitConverter.ToUInt64(data, recordOffset + 0x20) & 0x0000FFFFFFFFFFFFUL) != 0)
        {
            return false;
        }

        var attributeOffset = recordOffset + BitConverter.ToUInt16(data, recordOffset + 0x14);
        while (attributeOffset + 24 < recordOffset + recordSize)
        {
            var attributeType = BitConverter.ToUInt32(data, attributeOffset);
            if (attributeType == uint.MaxValue)
            {
                return false;
            }

            var attributeLength = BitConverter.ToUInt32(data, attributeOffset + 4);
            if (attributeLength == 0)
            {
                return false;
            }

            if (attributeType == 0x30 && data[attributeOffset + 8] == 0)
            {
                var valueOffset = BitConverter.ToUInt16(data, attributeOffset + 0x14);
                var value = attributeOffset + valueOffset;
                nameLength = data[value + 64];
                BitConverter.GetBytes(parentRecord).CopyTo(data, value);
                return true;
            }

            attributeOffset += checked((int)attributeLength);
        }

        return false;
    }

    static byte[] BuildSyntheticNtfs(int fileSize = 2 * 1024 * 1024)
    {
        var data = new byte[fileSize];
        data[3] = (byte)'N';
        data[4] = (byte)'T';
        data[5] = (byte)'F';
        data[6] = (byte)'S';
        data[0x0B] = 0x00;
        data[0x0C] = 0x02; // bytesPerSector = 512
        data[0x0D] = 0x08; // sectorsPerCluster = 8 (4096 bytes/cluster)
        data[0x30] = 0x01; // mftStart = cluster 1 (offset 4096)
        return data;
    }

    static void WriteFileRecord(byte[] data, int offset, ushort usn = 0x0001, uint recordSize = 1024)
    {
        data[offset] = 0x46;
        data[offset + 1] = 0x49;
        data[offset + 2] = 0x4C;
        data[offset + 3] = 0x45;
        var usaSize = (ushort)(recordSize / 512 + 1);
        data[offset + 4] = 0x30;
        data[offset + 5] = 0x00;
        data[offset + 6] = (byte)(usaSize & 0xFF);
        data[offset + 7] = (byte)(usaSize >> 8);
        var firstAttrOffset = (ushort)((48 + usaSize * 2 + 7) & ~7);
        data[offset + 0x14] = (byte)(firstAttrOffset & 0xFF);
        data[offset + 0x15] = (byte)(firstAttrOffset >> 8);
        data[offset + 0x16] = 0x01;
        BitConverter.GetBytes(recordSize).CopyTo(data, offset + 0x1C);
        data[offset + 48] = (byte)(usn & 0xFF);
        data[offset + 49] = (byte)(usn >> 8);
        var sectorCount = (int)(recordSize / 512);
        for (var i = 0; i < sectorCount; i++)
        {
            data[offset + 50 + i * 2] = 0x00;
            data[offset + 51 + i * 2] = 0x00;
            var sectorEnd = (i + 1) * 512 - 2;
            data[offset + sectorEnd] = (byte)(usn & 0xFF);
            data[offset + sectorEnd + 1] = (byte)(usn >> 8);
        }
    }

    static int WriteNonResidentDataAttribute(byte[] data, int offset, long fileSize, int clusterOffset,
        int clusterCount)
    {
        data[offset] = 0x80; // TypeCode = Data
        data[offset + 4] = 0x48; // RecordLength = 72
        data[offset + 8] = 0x01; // FormCode = non-resident
        data[offset + 0x20] = 0x40; // MappingPairsOffset
        var sizeBytes = BitConverter.GetBytes(fileSize);
        Array.Copy(sizeBytes, 0, data, offset + 0x28, 8);
        Array.Copy(sizeBytes, 0, data, offset + 0x30, 8);
        Array.Copy(sizeBytes, 0, data, offset + 0x38, 8);
        data[offset + 0x40] = 0x12;
        data[offset + 0x41] = (byte)(clusterCount & 0xFF);
        data[offset + 0x42] = (byte)((clusterCount >> 8) & 0xFF);
        data[offset + 0x43] = (byte)(clusterOffset & 0xFF);
        data[offset + 0x44] = 0x00;
        return 0x48;
    }

    static void WriteEndMarker(byte[] data, int offset)
    {
        data[offset] = 0xFF;
        data[offset + 1] = 0xFF;
        data[offset + 2] = 0xFF;
        data[offset + 3] = 0xFF;
    }
}
