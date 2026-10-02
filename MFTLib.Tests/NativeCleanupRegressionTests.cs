using System.Runtime.InteropServices;
using System.Text;
using MFTLib.Interop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class NativeCleanupRegressionTests
{
    [TestInitialize]
    public void Initialize()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Native volume and journal seams require Windows.");
        }

        NativeTestHooks.NativeResetTestState();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (OperatingSystem.IsWindows())
        {
            NativeTestHooks.NativeResetTestState();
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void VolumeRecordWithoutDataRuns_ReturnsError(bool resident)
    {
        var path = Path.GetTempFileName();
        try
        {
            const int recordOffset = 4096;
            const int attributeOffset = recordOffset + 0x38;
            var image = SyntheticNtfsImage.BuildBootSector(8192);
            SyntheticNtfsImage.WriteFileRecord(image, recordOffset);
            var attributeLength = SyntheticNtfsImage.WriteNonResidentDataAttribute(
                image, attributeOffset, 4096, 1, 1);
            image[attributeOffset + 8] = resident ? (byte)0 : (byte)1;
            image[attributeOffset + 0x40] = 0; // Empty mapping-pairs array.
            SyntheticNtfsImage.WriteEndMarker(image, attributeOffset + attributeLength);
            File.WriteAllBytes(path, image);
            NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);

            using var stream = File.OpenRead(path);
            var resultPointer = NativeTestHooks.NativeParseMFTRecordsRaw(
                stream.SafeFileHandle.DangerousGetHandle(), null, 0, 256);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<MftParseResult>(resultPointer);
                Assert.AreEqual("MFT record 0 has no data runs", result.ErrorMessage);
                Assert.AreEqual(0UL, result.UsedRecords);
                Assert.AreEqual(IntPtr.Zero, result.Entries);
            }
            finally
            {
                MFTLibNative._freeMftResult(resultPointer);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public unsafe void OversizedJournalName_ReportsCopiedLength(bool watch)
    {
        const int headerLength = 60;
        const int copiedLength = 259;
        var nameBytes = Encoding.Unicode.GetBytes(new string('x', 300));
        var recordLength = (headerLength + nameBytes.Length + 7) & ~7;
        var buffer = new byte[sizeof(long) + recordLength];
        using (var stream = new MemoryStream(buffer))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(2000L);
            writer.Write((uint)recordLength);
            writer.Write((ushort)2);
            writer.Write((ushort)0);
            writer.Write(100UL);
            writer.Write(5UL);
            writer.Write(1000L);
            writer.Write(0L);
            writer.Write(0x100U);
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(0U);
            writer.Write((ushort)nameBytes.Length);
            writer.Write((ushort)headerLength);
            writer.Write(nameBytes);
        }

        fixed (byte* data = buffer)
        {
            NativeTestHooks.NativeSetUsnIoSuccess(data, (uint)buffer.Length);
            using var handle = new SafeFileHandle(new IntPtr(1), false);
            var resultPointer = watch
                ? UncancelableUsnWatch.Read(handle, 500, 0xABCD)
                : MFTLibNative._readUsnJournal(handle, 500, 0xABCD, 1);
            Assert.AreNotEqual(IntPtr.Zero, resultPointer);
            try
            {
                var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPointer);
                Assert.AreEqual(string.Empty, result.ErrorMessage);
                Assert.AreEqual(1UL, result.EntryCount);
                Assert.AreEqual(copiedLength, (ushort)Marshal.ReadInt16(result.Entries, 40));
                Assert.AreEqual(new string('x', copiedLength),
                    Marshal.PtrToStringUni(result.Entries + 42, copiedLength));
                Assert.AreEqual((short)0, Marshal.ReadInt16(result.Entries, 42 + copiedLength * sizeof(char)));
            }
            finally
            {
                MFTLibNative._freeUsnJournalResult(resultPointer);
            }
        }
    }
}
