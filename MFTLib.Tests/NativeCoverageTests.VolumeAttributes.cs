using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

public partial class NativeCoverageTests
{
    // --- FindAttribute null return ---

    [TestMethod]
    public void ParseMFTRecords_WithNtfsHeaderButBadMagic_ReturnsError()
    {
        // Create a file that looks like NTFS boot sector but has invalid MFT record 0
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[1024 * 1024]; // 1MB  -  enough for boot sector + MFT area
            // Write "NTFS" at the expected name offset (byte 3 of BPB)
            data[3] = (byte)'N';
            data[4] = (byte)'T';
            data[5] = (byte)'F';
            data[6] = (byte)'S';
            // Set bytes per sector = 512
            data[0x0B] = 0x00;
            data[0x0C] = 0x02;
            // Set sectors per cluster = 8
            data[0x0D] = 0x08;
            // Set MFT start cluster = 0 (just after boot sector area  -  first cluster)
            // mftStart is at offset 0x30 (NTFS_BPB layout)
            data[0x30] = 0x01; // MFT at cluster 1 = byte 4096
            // Record 0 at byte 4096  -  leave magic as zeros (not "FILE")
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

    [TestMethod]
    public void ParseMFTRecords_WithValidMagicButNoDataAttribute_ReturnsError()
    {
        // Create a file with valid NTFS BPB + valid FILE magic in record 0,
        // but no Data attribute  -  triggers FindAttribute returning nullptr
        var path = Path.GetTempFileName();
        try
        {
            var data = new byte[1024 * 1024];
            // NTFS BPB
            data[3] = (byte)'N';
            data[4] = (byte)'T';
            data[5] = (byte)'F';
            data[6] = (byte)'S';
            data[0x0B] = 0x00;
            data[0x0C] = 0x02; // bytes per sector = 512
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
            data[attrOffset + 0] = 0xFF;
            data[attrOffset + 1] = 0xFF;
            data[attrOffset + 2] = 0xFF;
            data[attrOffset + 3] = 0xFF;

            // USA: write matching USN at sector ends
            var usn = (ushort)0x0001;
            // USA[0] = USN at offset 48
            data[recordOffset + 48] = (byte)(usn & 0xFF);
            data[recordOffset + 49] = (byte)(usn >> 8);
            // Sector 0 end (offset 510-511)
            data[recordOffset + 510] = (byte)(usn & 0xFF);
            data[recordOffset + 511] = (byte)(usn >> 8);
            // USA[1] at offset 50  -  original bytes
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

    [TestMethod]
    public void ParseMFTRecords_ZeroLengthAttribute_ReturnsMissingDataError()
    {
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);
            data[4096 + 0x38] = 0x10;
            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(
                fileStream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.ErrorMessage.Contains("Data attribute"));
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

    // NOTE: live-volume read-failure tests (ParseMFTRecords_ReadFailDuringParse,
    // _VolumeReadChunkFail_Returns, _ReadFailDuringExtensionRecords) were removed:
    // they opened \\.\C: and parsed the whole drive (36-89s each) to hit branches
    // that the synthetic-file siblings below (_VolumeReadChunkFail_ReturnsZeroUsedRecords,
    // _ReadFailDuringNonResidentAttributeList, _ReadFailDuringExtensionRecordRead)
    // already cover in milliseconds with no admin.

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
            data[3] = (byte)'N';
            data[4] = (byte)'T';
            data[5] = (byte)'F';
            data[6] = (byte)'S';
            data[0x0B] = 0x00;
            data[0x0C] = 0x02;
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

    // --- Resident AttributeList path ---

    [TestMethod]
    public void ParseMFTRecords_ResidentAttributeList_ParsesExtensionRecords()
    {
        // Record 0 with Data + empty resident AttributeList.
        // Exercises the resident else branch (lines 1017-1020).
        var path = Path.GetTempFileName();
        try
        {
            var data = BuildBootSector();
            WriteFileRecord(data, 4096);

            var a1 = 4096 + 0x38;
            var a1Len = WriteNonResidentDataAttribute(data, a1, 1024L * 1024, 1, 256);

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
            // 4=entries malloc, 5=strings malloc, 6=realloc when capacity exceeded
            MFTLibNative.NativeSetAllocFailCountdown(6);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.ErrorMessage.Contains("entry array"));
                Assert.IsTrue(result.UsedRecords > 0, "Should have partial results");
                Assert.IsTrue(result.UsedRecords < 5000, "Should not have all records");
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

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 4096);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(5000UL, result.TotalRecords);
                Assert.IsTrue(result.UsedRecords > 0);
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

    [TestMethod]
    public void ParseFromFile_MultiThreaded_WithFilter_TriggersSliceRealloc()
    {
        // With filter, slice initial capacity is 64  -  large buffer ensures enough
        // matching records per thread to exceed it.
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 5000, 4096);

            // Substring match on "file" should match all synthetic records
            var resultPointer = MFTLibNative._parseMftFromFile(path, "file", MatchFlags.Contains, 4096);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.IsTrue(result.UsedRecords > 0);
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
}
