using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib;
using MFTLib.Interop;

namespace MFTLib.Tests;

/// <summary>
/// Tests targeting native C++ code paths that are otherwise uncovered:
/// single-threaded fallback, allocation failures, Read failures, error branches.
/// </summary>
[TestClass]
public class NativeCoverageTests
{
    [TestCleanup]
    public void Cleanup()
    {
        MFTLibNative.NativeResetTestState();
    }

    // --- Single-threaded path (ProcessRecordBatch + fallback) ---

    [TestMethod]
    public void ParseFromFile_SingleThreaded_ProducesResults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            // Use 5000 records to exceed initial capacity (1024) and trigger realloc
            MftVolume.GenerateSyntheticMFT(path, 5000, 256);
            MFTLibNative.NativeSetMaxThreads(1);

            var resultPointer = MFTLibNative.ParseMFTFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(5000UL, result.TotalRecords);
                Assert.IsTrue(result.UsedRecords > 1024, "Should exceed initial capacity to trigger realloc");
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void ParseFromFile_SingleThreaded_WithFilter_FiltersResults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 5000, 256);
            MFTLibNative.NativeSetMaxThreads(1);

            var resultPointer = MFTLibNative.ParseMFTFromFile(path, ".git", MatchFlags.ExactMatch, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(5000UL, result.TotalRecords);
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void ParseFromFile_SingleThreaded_WithPaths_ResolvesPathEntries()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 5000, 256);
            MFTLibNative.NativeSetMaxThreads(1);

            var resultPointer = MFTLibNative.ParseMFTFromFile(path, null, MatchFlags.ResolvePaths, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.UsedRecords > 0);
                Assert.AreNotEqual(IntPtr.Zero, result.PathEntries);
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- Allocation failure paths ---

    [TestMethod]
    public void ParseFromFile_AllocFailOnResult_ReturnsNull()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 10, 256);

            // Fail the first calloc (result allocation)
            MFTLibNative.NativeSetAllocFailCountdown(1);
            var resultPointer = MFTLibNative.ParseMFTFromFile(path, null, MatchFlags.None, 256);
            Assert.AreEqual(IntPtr.Zero, resultPointer);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void ParseFromFile_AllocFailOnLookup_ReturnsErrorMessage()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 10, 256);

            // Fail the second alloc (lookup.init when resolving paths)
            MFTLibNative.NativeSetAllocFailCountdown(2);
            var resultPointer = MFTLibNative.ParseMFTFromFile(path, null, MatchFlags.ResolvePaths, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(errorMessage!.Contains("path lookup"));
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void ParseFromFile_AllocFailOnBuffers_ReturnsErrorMessage()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 10, 256);

            // Fail the VirtualAlloc for I/O buffers (alloc #2 without paths)
            MFTLibNative.NativeSetAllocFailCountdown(2);
            var resultPointer = MFTLibNative.ParseMFTFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(errorMessage!.Contains("I/O buffers"));
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void ParseFromFile_AllocFailOnEntries_ReturnsErrorMessage()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 10, 256);

            // Fail alloc #4 (malloc for entries — after calloc, VirtualAlloc x2)
            MFTLibNative.NativeSetAllocFailCountdown(4);
            var resultPointer = MFTLibNative.ParseMFTFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(errorMessage!.Contains("entry array"));
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void GenerateSyntheticMFT_AllocFail_ReturnsFalse()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MFTLibNative.NativeSetAllocFailCountdown(1);
            var success = MFTLibNative.GenerateSyntheticMFT(path, 10, 256);
            Assert.IsFalse(success);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }


    // --- Read failure paths ---

    [TestMethod]
    public void ParseFromFile_ReadFail_ReturnsZeroRecords()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 100, 256);

            // Fail the first ReadFile in FileReadChunk
            MFTLibNative.NativeSetReadFailCountdown(1);
            var resultPointer = MFTLibNative.ParseMFTFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(0UL, result.UsedRecords);
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- ParseMFTRecords error paths (via raw handle) ---

    [TestMethod]
    public void ParseMFTRecords_InvalidHandle_ReturnsErrorMessage()
    {
        var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(new IntPtr(-1), null, 0, 256);
        Assert.AreNotEqual(IntPtr.Zero, resultPointer);
        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
            var errorMessage = result.ErrorMessage;
            Assert.IsTrue(errorMessage!.Contains("invalid"));
        }
        finally
        {
            MFTLibNative.FreeMftResult(resultPointer);
        }
    }

    [TestMethod]
    public void ParseMFTRecords_NonNtfsFile_ReturnsNotNtfsError()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Write 4KB of garbage — enough for boot sector read, but not NTFS
            File.WriteAllBytes(path, new byte[4096]);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(errorMessage!.Contains("not NTFS") || errorMessage.Contains("boot sector"));
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void ParseMFTRecords_ReadFailOnBootSector_ReturnsError()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[4096]);
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Fail the first Read (boot sector)
            MFTLibNative.NativeSetReadFailCountdown(1);
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(errorMessage!.Contains("boot sector"));
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- ParseMFTFromFile error paths ---

    [TestMethod]
    public void ParseFromFile_NonexistentFile_ReturnsErrorMessage()
    {
        var resultPointer = MFTLibNative.ParseMFTFromFile(
            @"C:\nonexistent_file_12345.mft", null, MatchFlags.None, 256);
        Assert.AreNotEqual(IntPtr.Zero, resultPointer);
        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
            var errorMessage = result.ErrorMessage;
            Assert.IsTrue(errorMessage!.Contains("Failed to open file"));
        }
        finally
        {
            MFTLibNative.FreeMftResult(resultPointer);
        }
    }

    // --- Fixup mismatch path ---

    [TestMethod]
    public unsafe void ParseFromFile_CorruptedFixup_StillParses()
    {
        // Generate a synthetic MFT, then corrupt a sector-end checksum
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 20, 256);

            // Corrupt the fixup in record 10: overwrite the last 2 bytes of sector 0
            // (bytes 510-511) with a value that doesn't match the USN
            var data = File.ReadAllBytes(path);
            var recordOffset = 10 * 1024; // FILE_RECORD_SIZE = 1024
            // The USA offset is at bytes 4-5 of the record header
            var usaOffset = BitConverter.ToUInt16(data, recordOffset + 4);
            var usn = BitConverter.ToUInt16(data, recordOffset + usaOffset);
            // Write a different value at sector end (offset 510-511 within the record)
            var sectorEnd = recordOffset + 510;
            var badValue = (ushort)(usn ^ 0xFFFF); // guaranteed different
            data[sectorEnd] = (byte)(badValue & 0xFF);
            data[sectorEnd + 1] = (byte)(badValue >> 8);
            File.WriteAllBytes(path, data);

            var resultPointer = MFTLibNative.ParseMFTFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                // The parse should still complete — the corrupted record is skipped
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(20UL, result.TotalRecords);
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- FindAttribute null return ---

    [TestMethod]
    public void ParseMFTRecords_WithNtfsHeaderButBadMagic_ReturnsError()
    {
        // Create a file that looks like NTFS boot sector but has invalid MFT record 0
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[1024 * 1024]; // 1MB — enough for boot sector + MFT area
            // Write "NTFS" at the expected name offset (byte 3 of BPB)
            data[3] = (byte)'N';
            data[4] = (byte)'T';
            data[5] = (byte)'F';
            data[6] = (byte)'S';
            // Set bytes per sector = 512
            data[0x0B] = 0x00; data[0x0C] = 0x02;
            // Set sectors per cluster = 8
            data[0x0D] = 0x08;
            // Set MFT start cluster = 0 (just after boot sector area — first cluster)
            // mftStart is at offset 0x30 (NTFS_BPB layout)
            data[0x30] = 0x01; // MFT at cluster 1 = byte 4096
            // Record 0 at byte 4096 — leave magic as zeros (not "FILE")
            // This will trigger "Invalid MFT record 0 magic" error

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(
                    errorMessage!.Contains("magic") || errorMessage.Contains("MFT record 0"),
                    $"Unexpected error: {errorMessage}");
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void ParseMFTRecords_WithValidMagicButNoDataAttribute_ReturnsError()
    {
        // Create a file with valid NTFS BPB + valid FILE magic in record 0,
        // but no Data attribute — triggers FindAttribute returning nullptr
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[1024 * 1024];
            // NTFS BPB
            data[3] = (byte)'N'; data[4] = (byte)'T'; data[5] = (byte)'F'; data[6] = (byte)'S';
            data[0x0B] = 0x00; data[0x0C] = 0x02; // bytes per sector = 512
            data[0x0D] = 0x08; // sectors per cluster = 8
            data[0x30] = 0x01; // MFT at cluster 1 = byte 4096

            // Build a minimal FILE record at offset 4096
            var recordOffset = 4096;
            // Magic "FILE"
            data[recordOffset + 0] = 0x46; // F
            data[recordOffset + 1] = 0x49; // I
            data[recordOffset + 2] = 0x4C; // L
            data[recordOffset + 3] = 0x45; // E
            // USA offset (bytes 4-5) = 48
            data[recordOffset + 4] = 0x30;
            data[recordOffset + 5] = 0x00;
            // USA size (bytes 6-7) = 3 (USN + 2 sector entries)
            data[recordOffset + 6] = 0x03;
            data[recordOffset + 7] = 0x00;
            // First attribute offset (bytes 20-21 = offset 0x14) = 56
            data[recordOffset + 0x14] = 0x38;
            // Flags (bytes 22-23 = offset 0x16) = 0x0001 (in use)
            data[recordOffset + 0x16] = 0x01;

            // Place EndMarker attribute at offset 56 (no attributes before it)
            var attrOffset = recordOffset + 0x38;
            data[attrOffset + 0] = 0xFF; data[attrOffset + 1] = 0xFF;
            data[attrOffset + 2] = 0xFF; data[attrOffset + 3] = 0xFF;

            // USA: write matching USN at sector ends
            var usn = (ushort)0x0001;
            // USA[0] = USN at offset 48
            data[recordOffset + 48] = (byte)(usn & 0xFF);
            data[recordOffset + 49] = (byte)(usn >> 8);
            // Sector 0 end (offset 510-511)
            data[recordOffset + 510] = (byte)(usn & 0xFF);
            data[recordOffset + 511] = (byte)(usn >> 8);
            // USA[1] at offset 50 — original bytes
            data[recordOffset + 50] = 0x00;
            data[recordOffset + 51] = 0x00;

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                var errorMessage = result.ErrorMessage;
                Assert.IsTrue(
                    errorMessage!.Contains("Data attribute") || errorMessage.Contains("magic"),
                    $"Unexpected error: {errorMessage}");
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- ReadMFTRecord failure + VolumeReadChunk failure paths ---

    [TestCategory("RequiresAdmin")]
    [TestMethod]
    public void ParseMFTRecords_ReadFailDuringParse_HandlesGracefully()
    {
        if (!ElevationUtilities.IsElevated())
            Assert.Inconclusive("Requires admin elevation");

        using var handle = FileUtilities.GetVolumeHandle(@"\\.\C:");
        // Fail the 3rd read — after boot sector and record 0, this hits VolumeReadChunk
        MFTLibNative.NativeSetReadFailCountdown(3);
        var resultPointer = MFTLibNative.ParseMFTRecords(handle, null, MatchFlags.None, 256);
        Assert.AreNotEqual(IntPtr.Zero, resultPointer);
        MFTLibNative.FreeMftResult(resultPointer);
    }

    // --- ParseMFTRecords Read failure on MFT record 0 ---

    [TestMethod]
    public void ParseMFTRecords_ReadFailOnMftRecord0_ReturnsError()
    {
        // Create a file with valid NTFS BPB so boot sector read succeeds,
        // then fail the 2nd Read (MFT record 0)
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[1024 * 1024];
            data[3] = (byte)'N'; data[4] = (byte)'T'; data[5] = (byte)'F'; data[6] = (byte)'S';
            data[0x0B] = 0x00; data[0x0C] = 0x02;
            data[0x0D] = 0x08;
            data[0x30] = 0x01;
            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            MFTLibNative.NativeSetReadFailCountdown(2); // fail 2nd Read
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.ErrorMessage.Contains("MFT record 0"));
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- VolumeReadChunk Read failure ---

    [TestCategory("RequiresAdmin")]
    [TestMethod]
    public void ParseMFTRecords_VolumeReadChunkFail_Returns()
    {
        if (!ElevationUtilities.IsElevated())
            Assert.Inconclusive("Requires admin elevation");

        using var handle = FileUtilities.GetVolumeHandle(@"\\.\C:");
        // Fail the 4th read — boot sector, record 0, possibly extension reads, then VolumeReadChunk
        MFTLibNative.NativeSetReadFailCountdown(4);
        var resultPointer = MFTLibNative.ParseMFTRecords(handle, null, MatchFlags.None, 256);
        Assert.AreNotEqual(IntPtr.Zero, resultPointer);
        MFTLibNative.FreeMftResult(resultPointer);
    }

    // --- ReadNonResidentData + ReadMFTRecord failure paths ---

    [TestCategory("RequiresAdmin")]
    [TestMethod]
    public void ParseMFTRecords_ReadFailDuringExtensionRecords_Returns()
    {
        if (!ElevationUtilities.IsElevated())
            Assert.Inconclusive("Requires admin elevation");

        // Fail reads at various points during extension record handling.
        // Covers: ReadNonResidentData failure (lines 138-140),
        //         ReadMFTRecord failure (lines 162, 166-170),
        //         VolumeReadChunk failure (line 950),
        //         Extension record handling (lines 1042, 1050-1052)
        // The exact countdown depends on MFT structure, so sweep a range.
        for (var countdown = 3; countdown <= 50; countdown++)
        {
            using var handle = FileUtilities.GetVolumeHandle(@"\\.\C:");
            MFTLibNative.NativeSetReadFailCountdown(countdown);
            var resultPointer = MFTLibNative.ParseMFTRecords(handle, null, MatchFlags.None, 256);
            if (resultPointer != IntPtr.Zero)
                MFTLibNative.FreeMftResult(resultPointer);
        }
    }

    // --- Resident AttributeList path ---

    [TestMethod]
    public unsafe void ParseMFTRecords_ResidentAttributeList_ParsesExtensionRecords()
    {
        // Record 0 with Data + empty resident AttributeList.
        // Exercises the resident else branch (lines 1017-1020).
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildSyntheticNtfs();
            WriteFileRecord(data, 4096);

            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, clusterOffset: 1, clusterCount: 256);

            // Resident AttributeList with empty value (no extension records)
            var a2 = a1 + a1Len;
            data[a2] = 0x20; // TypeCode = AttributeList
            data[a2 + 4] = 0x20; // RecordLength = 32
            data[a2 + 8] = 0x00; // FormCode = resident
            data[a2 + 0x10] = 0x00; // ValueLength = 0
            data[a2 + 0x14] = 0x18; // ValueOffset

            WriteEndMarker(data, a2 + 0x20);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.TotalRecords > 0);
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- Single-threaded realloc failure in ProcessRecordBatch ---

    [TestMethod]
    public void ParseFromFile_SingleThreaded_AllocFailOnRealloc_ReturnsPartialResults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            // 5000 records with initial capacity 1024 → realloc triggers around record ~1024
            MftVolume.GenerateSyntheticMFT(path, 5000, 256);
            MFTLibNative.NativeSetMaxThreads(1);

            // Alloc countdown: 1=result calloc, 2=VirtualAlloc buf0, 3=VirtualAlloc buf1,
            // 4=entries malloc, 5=realloc when capacity exceeded
            MFTLibNative.NativeSetAllocFailCountdown(5);

            var resultPointer = MFTLibNative.ParseMFTFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.ErrorMessage!.Contains("entry array"));
                Assert.IsTrue(result.UsedRecords > 0, "Should have partial results");
                Assert.IsTrue(result.UsedRecords < 5000, "Should not have all records");
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- Multi-threaded path: slice realloc + merge realloc ---

    [TestMethod]
    public void ParseFromFile_MultiThreaded_ProducesResults()
    {
        // Exercises the multi-threaded ProcessRecordSlice path (lines 629-630 realloc)
        // and ParseMFTImpl merge realloc (lines 854-855).
        // Large buffer (4096) ensures each thread handles many records, overflowing
        // the initial per-slice capacity ((records/threads)/4) and triggering realloc.
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 5000, 4096);

            var resultPointer = MFTLibNative.ParseMFTFromFile(path, null, MatchFlags.None, 4096);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(5000UL, result.TotalRecords);
                Assert.IsTrue(result.UsedRecords > 0);
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void ParseFromFile_MultiThreaded_WithFilter_TriggersSliceRealloc()
    {
        // With filter, slice initial capacity is 64 — large buffer ensures enough
        // matching records per thread to exceed it.
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 5000, 4096);

            // Substring match on "file" should match all synthetic records
            var resultPointer = MFTLibNative.ParseMFTFromFile(path, "file", MatchFlags.Contains, 4096);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.UsedRecords > 0);
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- ReadNonResidentData success path ---

    [TestMethod]
    public unsafe void ParseMFTRecords_NonResidentAttributeList_Succeeds()
    {
        // Non-resident AttributeList with data runs pointing to valid data in the file.
        // Exercises ReadNonResidentData success path (lines 142-145, 147).
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildSyntheticNtfs();
            WriteFileRecord(data, 4096);

            // Attribute 1: Data (non-resident) → clusters 1..256 (1MB)
            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, clusterOffset: 1, clusterCount: 256);

            // Attribute 2: Non-resident AttributeList with data at cluster 260
            // The data at cluster 260 is all zeros → empty/invalid entries → no extensions
            var a2 = a1 + a1Len;
            data[a2] = 0x20; // TypeCode = AttributeList
            data[a2 + 4] = 0x48; // RecordLength = 72
            data[a2 + 8] = 0x01; // FormCode = non-resident
            data[a2 + 0x20] = 0x40; // MappingPairsOffset
            // Size = 512 bytes (small, but enough to test the read path)
            var attrListSize = BitConverter.GetBytes(512L);
            Array.Copy(attrListSize, 0, data, a2 + 0x28, 8); // AllocatedLength
            Array.Copy(attrListSize, 0, data, a2 + 0x30, 8); // FileSize
            Array.Copy(attrListSize, 0, data, a2 + 0x38, 8); // ValidDataLength
            // Data run: cluster 260, 1 cluster — within our 2MB file (260*4096 = 1,064,960 < 2MB)
            data[a2 + 0x40] = 0x12; // 2-byte length, 1-byte offset
            data[a2 + 0x41] = 0x01; data[a2 + 0x42] = 0x00; // 1 cluster
            data[a2 + 0x43] = 0x80; // cluster 128 (offset 524,288 — within 2MB)
            data[a2 + 0x44] = 0x00; // terminator

            WriteEndMarker(data, a2 + 0x48);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.TotalRecords > 0);
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- Extension record Data attribute parsing ---

    [TestMethod]
    public unsafe void ParseMFTRecords_ResidentAttributeList_WithExtensionRecord()
    {
        // Record 0 has Data + resident AttributeList pointing to record 1.
        // Record 1 has a Data attribute with additional data runs.
        // Exercises lines 1042, 1050-1052 (extension record Data attribute parsing).
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildSyntheticNtfs();

            // --- Record 0 at offset 4096 ---
            WriteFileRecord(data, 4096);

            // Attribute 1: Data (non-resident) → clusters 1..256 (1MB)
            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, clusterOffset: 1, clusterCount: 256);

            // Attribute 2: AttributeList (resident) with one entry pointing to segment 1
            var a2 = a1 + a1Len;
            var a2Len = WriteResidentAttributeList(data, a2, segmentNumber: 1);

            // End marker
            WriteEndMarker(data, a2 + a2Len);

            // --- Record 1 at offset 5120 (4096 + 1024) ---
            WriteFileRecord(data, 5120, usn: 0x0002);

            // Extension record Data attribute → cluster 300, 1 cluster (4KB = 4 records)
            var ext1 = 5120 + 0x38;
            var ext1Len = WriteNonResidentDataAttribute(data, ext1, 4096, clusterOffset: 300, clusterCount: 1);
            WriteEndMarker(data, ext1 + ext1Len);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                // Should succeed — extension record adds 4 more records (1024 + 4)
                Assert.IsTrue(result.TotalRecords > 0);
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- ReadMFTRecord: record not found in data runs ---

    [TestMethod]
    public unsafe void ParseMFTRecords_ExtensionRecordNotInRuns_StillParses()
    {
        // AttributeList points to segment 9999 which is beyond the data runs.
        // Exercises lines 166-170 (ReadMFTRecord "not found").
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildSyntheticNtfs();
            WriteFileRecord(data, 4096);

            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, clusterOffset: 1, clusterCount: 256);

            var a2 = a1 + a1Len;
            var a2Len = WriteResidentAttributeList(data, a2, segmentNumber: 9999);

            WriteEndMarker(data, a2 + a2Len);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                // Should still parse — failed extension read is skipped
                Assert.IsTrue(result.TotalRecords > 0);
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- ReadMFTRecord: Read failure during extension record ---

    [TestMethod]
    public unsafe void ParseMFTRecords_ReadFailDuringExtensionRecordRead()
    {
        // Covers line 162 (ReadMFTRecord Read failure).
        // Reads: 1=boot sector, 2=record 0, 3=ReadMFTRecord for extension → fail here
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildSyntheticNtfs();
            WriteFileRecord(data, 4096);

            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, clusterOffset: 1, clusterCount: 256);

            var a2 = a1 + a1Len;
            var a2Len = WriteResidentAttributeList(data, a2, segmentNumber: 1);

            WriteEndMarker(data, a2 + a2Len);
            WriteFileRecord(data, 5120, usn: 0x0002);
            var ext1 = 5120 + 0x38;
            WriteEndMarker(data, ext1); // Just end marker — no Data attribute on extension record

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            MFTLibNative.NativeSetReadFailCountdown(3); // Fail 3rd read = ReadMFTRecord
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.TotalRecords > 0);
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- VolumeReadChunk Read failure ---

    [TestMethod]
    public unsafe void ParseMFTRecords_VolumeReadChunkFail_ReturnsZeroUsedRecords()
    {
        // Covers line 950 (VolumeReadChunk returns 0 on Read failure).
        // With no attribute list, reads: 1=boot sector, 2=record 0, 3=VolumeReadChunk → fail
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildSyntheticNtfs();
            WriteFileRecord(data, 4096);

            // Just a Data attribute, no AttributeList → straight to VolumeReadChunk
            var a1 = 4096 + 0x38;
            WriteNonResidentDataAttribute(data, a1, 1024L * 1024, clusterOffset: 1, clusterCount: 256);
            WriteEndMarker(data, a1 + 0x48);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            MFTLibNative.NativeSetReadFailCountdown(3); // Fail 3rd read = VolumeReadChunk
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(0UL, result.UsedRecords);
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- ReadNonResidentData failure ---

    [TestMethod]
    public unsafe void ParseMFTRecords_ReadFailDuringNonResidentAttributeList()
    {
        // Covers lines 138-140 (ReadNonResidentData Read failure).
        // Record 0 has non-resident AttributeList. Read #3 targets its data → fail.
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildSyntheticNtfs();
            WriteFileRecord(data, 4096);

            // Attribute 1: Data (non-resident) → clusters 1..256
            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, clusterOffset: 1, clusterCount: 256);

            // Attribute 2: Non-resident AttributeList (FormCode=1)
            var a2 = a1 + a1Len;
            // TypeCode = AttributeList (0x20)
            data[a2] = 0x20;
            // RecordLength = 72 (0x48)
            data[a2 + 4] = 0x48;
            // FormCode = 1 (non-resident)
            data[a2 + 8] = 0x01;
            // MappingPairsOffset = 0x40
            data[a2 + 0x20] = 0x40;
            // FileSize = 4096 (one cluster of attribute list data)
            var attrListSize = BitConverter.GetBytes(4096L);
            Array.Copy(attrListSize, 0, data, a2 + 0x28, 8); // AllocatedLength
            Array.Copy(attrListSize, 0, data, a2 + 0x30, 8); // FileSize
            Array.Copy(attrListSize, 0, data, a2 + 0x38, 8); // ValidDataLength
            // Data run: cluster 300, 1 cluster
            data[a2 + 0x40] = 0x11; // 1-byte length, 1-byte offset
            data[a2 + 0x41] = 0x01; // 1 cluster
            data[a2 + 0x42] = 0x80; // cluster 128 — use unsigned to avoid sign-extend issues
            data[a2 + 0x43] = 0x00; // terminator

            WriteEndMarker(data, a2 + 0x48);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            MFTLibNative.NativeSetReadFailCountdown(3); // Fail 3rd read = ReadNonResidentData
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                // Parse continues even if attribute list read fails
                Assert.IsTrue(result.TotalRecords > 0);
            }
            finally
            {
                MFTLibNative.FreeMftResult(resultPointer);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- FreeMftResult null safety ---

    [TestMethod]
    public void FreeMftResult_NullPointer_DoesNotThrow()
    {
        MFTLibNative.FreeMftResult(IntPtr.Zero);
    }

    // --- Helpers for building synthetic NTFS data ---

    static byte[] BuildSyntheticNtfs(int fileSize = 2 * 1024 * 1024)
    {
        var data = new byte[fileSize];
        data[3] = (byte)'N'; data[4] = (byte)'T'; data[5] = (byte)'F'; data[6] = (byte)'S';
        data[0x0B] = 0x00; data[0x0C] = 0x02; // bytesPerSector = 512
        data[0x0D] = 0x08; // sectorsPerCluster = 8 (4096 bytes/cluster)
        data[0x30] = 0x01; // mftStart = cluster 1 (offset 4096)
        return data;
    }

    static void WriteFileRecord(byte[] data, int offset, ushort usn = 0x0001)
    {
        // Magic "FILE"
        data[offset] = 0x46; data[offset + 1] = 0x49; data[offset + 2] = 0x4C; data[offset + 3] = 0x45;
        // USA offset = 48, USA size = 3
        data[offset + 4] = 0x30; data[offset + 5] = 0x00;
        data[offset + 6] = 0x03; data[offset + 7] = 0x00;
        // First attribute offset = 56 (0x38)
        data[offset + 0x14] = 0x38; data[offset + 0x15] = 0x00;
        // Flags = in use
        data[offset + 0x16] = 0x01;
        // USA entries
        data[offset + 48] = (byte)(usn & 0xFF); data[offset + 49] = (byte)(usn >> 8);
        data[offset + 50] = 0x00; data[offset + 51] = 0x00; // original sector 0 end bytes
        data[offset + 52] = 0x00; data[offset + 53] = 0x00; // original sector 1 end bytes
        // Write USN at sector boundaries
        data[offset + 510] = (byte)(usn & 0xFF); data[offset + 511] = (byte)(usn >> 8);
        data[offset + 1022] = (byte)(usn & 0xFF); data[offset + 1023] = (byte)(usn >> 8);
    }

    static int WriteNonResidentDataAttribute(byte[] data, int offset, long fileSize, int clusterOffset, int clusterCount)
    {
        data[offset] = 0x80; // TypeCode = Data
        data[offset + 4] = 0x48; // RecordLength = 72
        data[offset + 8] = 0x01; // FormCode = non-resident
        data[offset + 0x20] = 0x40; // MappingPairsOffset
        var sizeBytes = BitConverter.GetBytes(fileSize);
        Array.Copy(sizeBytes, 0, data, offset + 0x28, 8); // AllocatedLength
        Array.Copy(sizeBytes, 0, data, offset + 0x30, 8); // FileSize
        Array.Copy(sizeBytes, 0, data, offset + 0x38, 8); // ValidDataLength
        // Data run: 0x12 = 2-byte length, 1-byte offset
        data[offset + 0x40] = 0x12;
        data[offset + 0x41] = (byte)(clusterCount & 0xFF);
        data[offset + 0x42] = (byte)((clusterCount >> 8) & 0xFF);
        data[offset + 0x43] = (byte)(clusterOffset & 0xFF);
        data[offset + 0x44] = 0x00; // terminator
        return 0x48;
    }

    static int WriteResidentAttributeList(byte[] data, int offset, uint segmentNumber)
    {
        // ATTRIBUTE_LIST_ENTRY is 26 bytes minimum (with 1-char name)
        // We use 28 bytes (no name, padded)
        const int entrySize = 28;
        const int valueOffset = 0x18; // header is 24 bytes for resident attribute

        data[offset] = 0x20; // TypeCode = AttributeList
        data[offset + 8] = 0x00; // FormCode = resident
        // ValueLength
        data[offset + 0x10] = (byte)(entrySize & 0xFF);
        data[offset + 0x11] = (byte)((entrySize >> 8) & 0xFF);
        // ValueOffset
        data[offset + 0x14] = (byte)valueOffset;

        // Write the ATTRIBUTE_LIST_ENTRY at valueOffset
        var entry = offset + valueOffset;
        data[entry] = 0x80; // AttributeTypeCode = Data
        data[entry + 4] = (byte)(entrySize & 0xFF); // RecordLength
        data[entry + 5] = (byte)((entrySize >> 8) & 0xFF);
        // SegmentReference at offset 16 within entry
        data[entry + 16] = (byte)(segmentNumber & 0xFF);
        data[entry + 17] = (byte)((segmentNumber >> 8) & 0xFF);
        data[entry + 18] = (byte)((segmentNumber >> 16) & 0xFF);
        data[entry + 19] = (byte)((segmentNumber >> 24) & 0xFF);

        var totalLength = valueOffset + entrySize;
        // Round up to multiple of 8
        totalLength = (totalLength + 7) & ~7;
        data[offset + 4] = (byte)(totalLength & 0xFF);
        data[offset + 5] = (byte)((totalLength >> 8) & 0xFF);
        return totalLength;
    }

    static void WriteEndMarker(byte[] data, int offset)
    {
        data[offset] = 0xFF; data[offset + 1] = 0xFF;
        data[offset + 2] = 0xFF; data[offset + 3] = 0xFF;
    }
}
