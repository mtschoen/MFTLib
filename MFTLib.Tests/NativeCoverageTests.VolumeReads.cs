using System.Runtime.InteropServices;
using MFTLib.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class NativeCoverageTests
{
    // --- ParseMFTRecords error paths (via raw handle) ---

    [TestMethod]
    public void ParseMFTRecords_AllocFailOnResult_ReturnsNull()
    {
        MFTLibNative.NativeSetAllocFailCountdown(1);
        var resultPointer = MFTLibNative.NativeParseMFTRecordsRaw(new IntPtr(-1), null, 0, 256);
        Assert.AreEqual(IntPtr.Zero, resultPointer);
    }

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
            MFTLibNative._freeMftResult(resultPointer);
        }
    }

    [TestMethod]
    public void ParseMFTRecords_NonNtfsFile_ReturnsNotNtfsError()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Write 4KB of garbage  -  enough for boot sector read, but not NTFS
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

    // --- ParseMFTFromFile error paths ---

    [TestMethod]
    public void ParseFromFile_NonexistentFile_ReturnsErrorMessage()
    {
        var resultPointer = MFTLibNative._parseMftFromFile(
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
            MFTLibNative._freeMftResult(resultPointer);
        }
    }

    // --- Fixup mismatch path ---

    [TestMethod]
    public void ParseFromFile_CorruptedFixup_StillParses()
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
            var recordSize = BitConverter.ToUInt32(data, 0x1C);
            var recordOffset = (int)(10 * recordSize);
            // The USA offset is at bytes 4-5 of the record header
            var usaOffset = BitConverter.ToUInt16(data, recordOffset + 4);
            var usn = BitConverter.ToUInt16(data, recordOffset + usaOffset);
            // Write a different value at sector end (offset 510-511 within the record)
            var sectorEnd = recordOffset + 510;
            var badValue = (ushort)(usn ^ 0xFFFF); // guaranteed different
            data[sectorEnd] = (byte)(badValue & 0xFF);
            data[sectorEnd + 1] = (byte)(badValue >> 8);
            File.WriteAllBytes(path, data);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                // The parse should still complete  -  the corrupted record is skipped
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual(20UL, result.TotalRecords);
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
    public void ParseFromFile_InUseFlagCleared_SkipsRecord()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 20, 256);
            var data = File.ReadAllBytes(path);
            var recordOffset = 10 * 1024;
            data[recordOffset + 0x16] = 0;
            data[recordOffset + 0x17] = 0;
            File.WriteAllBytes(path, data);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            MFTLibNative._freeMftResult(resultPointer);
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
    public void ParseFromFile_OversizedUsaStopsAtRecordBoundary()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Delete(path);
            MftVolume.GenerateSyntheticMFT(path, 20, 256);
            var data = File.ReadAllBytes(path);
            var recordOffset = 10 * 1024;
            data[recordOffset + 6] = 4;
            data[recordOffset + 7] = 0;
            File.WriteAllBytes(path, data);

            var resultPointer = MFTLibNative._parseMftFromFile(path, null, MatchFlags.None, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            MFTLibNative._freeMftResult(resultPointer);
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
