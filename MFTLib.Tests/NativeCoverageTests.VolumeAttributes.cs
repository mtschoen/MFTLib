using System.Runtime.InteropServices;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
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
            var data = BuildBootSector();

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
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
            var data = BuildBootSector();
            const int recordOffset = 4096;
            WriteFileRecord(data, recordOffset);
            WriteEndMarker(data, recordOffset + 0x38);

            File.WriteAllBytes(path, data);

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
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
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
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
            NativeTestHooks.NativeSetReadFailCountdown(2); // fail 2nd Read
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
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
    public void ParseMFTRecords_ResidentAttributeList_EmptyValueReturnsDataRecords()
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
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
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
            NativeTestHooks.NativeSetMaxThreads(1);

            // Alloc countdown: 1=result calloc, 2=VirtualAlloc buf0, 3=VirtualAlloc buf1,
            // 4=entries malloc, 5=strings malloc, 6=realloc when capacity exceeded
            NativeTestHooks.NativeSetAllocFailCountdown(6);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256, IntPtr.Zero, null);
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

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 4096, IntPtr.Zero, null);
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
            var resultPointer = MFTLibNative._parseMftFromFile(path, "file", MatchFlags.Contains, 4096, IntPtr.Zero, null);
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
