using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.SyntheticNtfsImage;

namespace MFTLib.Tests;

public partial class NativeMftProcessingTests
{
    // --- Variable record size and geometry validation tests ---

    [TestMethod]
    public void ParseMFTRecords_VolumeRecordSizeOverride_InvalidSize_ReturnsError()
    {
        try
        {
            // Override with unsupported record size 1536
            NativeTestHooks.NativeSetVolumeRecordSizeOverride(1536);

            var data = BuildBootSector();
            WriteFileRecord(data, 4096);

            var result = NativeTestHooks.ParseImage(data);
            Assert.IsTrue(result.ErrorMessage.Contains("record size", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            NativeTestHooks.NativeResetTestState();
        }
    }

    [TestMethod]
    public void ParseMFTRecords_VolumeRecordSizeOverride_Supported1024_Succeeds()
    {
        try
        {
            NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);

            var data = BuildBootSector();
            WriteFileRecord(data, 4096, recordSize: 1024);
            const int dataAttribute = 4096 + 0x38;
            var dataLength = WriteNonResidentDataAttribute(
                data, dataAttribute, 1024L * 1024, 1, 256);
            WriteEndMarker(data, dataAttribute + dataLength);

            var result = NativeTestHooks.ParseImage(data);
            Assert.AreEqual(string.Empty, result.ErrorMessage);
            Assert.IsGreaterThan(0UL, result.TotalRecords);
        }
        finally
        {
            NativeTestHooks.NativeResetTestState();
        }
    }

    [TestMethod]
    public void ParseMFTRecords_VolumeRecordSizeOverride_Supported4096_Succeeds()
    {
        try
        {
            NativeTestHooks.NativeSetVolumeRecordSizeOverride(4096);

            var data = BuildBootSector();
            WriteFileRecord(data, 4096, 0x0001, 4096);
            const int dataAttribute = 4096 + 0x48;
            var dataLength = WriteNonResidentDataAttribute(
                data, dataAttribute, 1024L * 1024, 1, 256);
            WriteEndMarker(data, dataAttribute + dataLength);

            var result = NativeTestHooks.ParseImage(data);
            Assert.AreEqual(string.Empty, result.ErrorMessage);
            Assert.AreEqual(256UL, result.TotalRecords);
        }
        finally
        {
            NativeTestHooks.NativeResetTestState();
        }
    }

    [TestMethod]
    public void ResetTestState_ClearsVolumeRecordSizeOverride()
    {
        try
        {
            NativeTestHooks.NativeSetVolumeRecordSizeOverride(1536);
            NativeTestHooks.NativeResetTestState();

            var data = BuildBootSector();
            WriteFileRecord(data, 4096, recordSize: 1024);
            const int dataAttribute = 4096 + 0x38;
            var dataLength = WriteNonResidentDataAttribute(
                data, dataAttribute, 1024L * 1024, 1, 256);
            WriteEndMarker(data, dataAttribute + dataLength);

            var result = NativeTestHooks.ParseImage(data);
            Assert.IsFalse(result.ErrorMessage.Contains("record size", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            NativeTestHooks.NativeResetTestState();
        }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [DataRow((byte)0xF6, true, DisplayName = "-10 declares 1024-byte records")]
    [DataRow((byte)0x00, false, DisplayName = "zero declares no record size")]
    [DataRow((byte)0xE0, false, DisplayName = "-32 declares a size past the supported range")]
    public void ParseMFTRecords_OffWindowsWithoutAnOverride_TakesTheRecordSizeFromTheBootSector(
        byte clustersPerFileRecord, bool parses)
    {
        try
        {
            var data = BuildBootSector();
            data[0x40] = clustersPerFileRecord;
            WriteFileRecord(data, 4096, recordSize: 1024);
            const int dataAttribute = 4096 + 0x38;
            var dataLength = WriteNonResidentDataAttribute(
                data, dataAttribute, 1024L * 1024, 1, 256);
            WriteEndMarker(data, dataAttribute + dataLength);

            var result = NativeTestHooks.ParseImage(data);
            if (parses)
            {
                Assert.AreEqual(string.Empty, result.ErrorMessage);
                Assert.AreEqual(1024UL, result.TotalRecords);
            }
            else
            {
                Assert.AreEqual("Failed to query volume record size or unsupported record size", result.ErrorMessage);
            }
        }
        finally
        {
            NativeTestHooks.NativeResetTestState();
        }
    }
}
