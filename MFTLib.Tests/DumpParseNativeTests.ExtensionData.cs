using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class DumpParseNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(10, 24, 32u, 2u, false, 1024, 123456789L, DisplayName = "Issue895_AfterBaseAcrossSlices")]
    [DataRow(24, 10, 32u, 2u, false, 1024, 123456789L, DisplayName = "Issue895_BeforeBaseAcrossSlices")]
    [DataRow(10, 24, 8u, 1u, false, 1024, 123456789L, DisplayName = "Issue895_AfterBaseAcrossReadsSerial")]
    [DataRow(24, 10, 8u, 2u, false, 1024, 123456789L, DisplayName = "Issue895_BeforeBaseAcrossReadsParallel")]
    [DataRow(10, 24, 8u, 1u, true, 1024, 37L, DisplayName = "Issue895_ResidentExtension")]
    [DataRow(10, 24, 8u, 2u, false, 4096, 123456789L, DisplayName = "Issue895_LargeRecordGeometry")]
    [DataRow(0, 24, 8u, 1u, false, 1024, 123456789L, DisplayName = "Issue895_BaseRecordZero")]
    [DataRow(10, 24, 8u, 1u, false, 1024, 0L, DisplayName = "Issue895_EmptyExtensionClearsUnknown")]
    [TestCategory("Smoke")]
    public void Parse_ExtensionData_ResolvesSizeAcrossSlicesAndReads(int baseRecordNumber,
        int extensionRecordNumber, uint bufferSizeRecords, uint threadCount, bool resident, int recordSize, long expectedSize)
    {
        var path = MftDumpFixture.WriteFile(TemporaryDirectoryPath,
            ExtensionDataFixture.Build(baseRecordNumber, extensionRecordNumber, recordSize,
                new ExtensionDataFixture.Options { Resident = resident, Size = expectedSize }));
        NativeTestHooks.NativeSetMaxThreads(threadCount);

        var records = DirectParse.ParseFile(path, out _, bufferSizeRecords);

        var entry = records.Single(record => record.RecordNumber == (ulong)baseRecordNumber);
        Assert.AreEqual(expectedSize, entry.Size);
        Assert.IsTrue(entry.SizeKnown);
        Assert.DoesNotContain(record => record.RecordNumber == (ulong)extensionRecordNumber, records);
    }

    [TestMethod]
    [DataRow("continuation", DisplayName = "Issue895_LaterExtentIgnored")]
    [DataRow("sequence", DisplayName = "Issue895_ReusedBaseIgnored")]
    [DataRow("freed", DisplayName = "Issue895_FreedExtensionIgnored")]
    [DataRow("named", DisplayName = "Issue895_NamedStreamIgnored")]
    [DataRow("truncated", DisplayName = "Issue895_TruncatedDataIgnored")]
    [DataRow("tail", DisplayName = "Issue895_MalformedTailIgnored")]
    [DataRow("missing", DisplayName = "Issue895_NoDataIgnored")]
    [DataRow("negative", DisplayName = "Issue895_NegativeSizeIgnored")]
    [DataRow("missing-before", DisplayName = "Issue895_MissingBaseBeforeRows")]
    [DataRow("missing-after", DisplayName = "Issue895_MissingBaseAfterRows")]
    [DataRow("high-reference", DisplayName = "Issue895_HighBaseReferenceNotTruncated")]
    public void Parse_IneligibleExtension_KeepsSizeUnknown(string scenario)
    {
        var options = scenario switch
        {
            "continuation" => new ExtensionDataFixture.Options { LowestVcn = 1 },
            "sequence" => new ExtensionDataFixture.Options { BaseSequenceNumber = 2 },
            "freed" => new ExtensionDataFixture.Options { InUse = false },
            "named" => new ExtensionDataFixture.Options { Named = true },
            "truncated" => new ExtensionDataFixture.Options { Truncated = true },
            "tail" => new ExtensionDataFixture.Options { MalformedTail = true },
            "missing" => new ExtensionDataFixture.Options { MissingData = true },
            "negative" => new ExtensionDataFixture.Options { Size = -1 },
            "missing-before" => new ExtensionDataFixture.Options { ReferencedRecordNumber = 1 },
            "missing-after" => new ExtensionDataFixture.Options { ReferencedRecordNumber = 100 },
            "high-reference" => new ExtensionDataFixture.Options { ReferencedRecordNumber = (1UL << 32) | 10 },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var path = MftDumpFixture.WriteFile(TemporaryDirectoryPath, ExtensionDataFixture.Build(options: options));

        var records = DirectParse.ParseFile(path, out _, 8);

        var entry = records.Single(record => record.RecordNumber == 10);
        Assert.AreEqual(0L, entry.Size);
        Assert.IsFalse(entry.SizeKnown);
    }

    [TestMethod]
    [DataRow(false, 17L, DisplayName = "Issue895_KnownBaseNotOverwritten")]
    [DataRow(true, 0L, DisplayName = "Issue895_DirectoryNotOverwritten")]
    public void Parse_ExtensionData_PreservesKnownBaseSize(bool directory, long expectedSize)
    {
        var options = new ExtensionDataFixture.Options
        {
            Base = new DumpRecord("linked.bin", 5) { IsDirectory = directory, Size = 17 }
        };
        var path = MftDumpFixture.WriteFile(TemporaryDirectoryPath, ExtensionDataFixture.Build(options: options));

        var records = DirectParse.ParseFile(path, out _, 8);

        var entry = records.Single(record => record.RecordNumber == 10);
        Assert.AreEqual(expectedSize, entry.Size);
        Assert.IsTrue(entry.SizeKnown);
    }

    [TestMethod]
    public async Task FileIndex_ExtensionData_ExposesKnownFileSize_Issue895()
    {
        var path = MftDumpFixture.WriteFile(TemporaryDirectoryPath, ExtensionDataFixture.Build());
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            MftSource = MftIndexSources.FromMftDumpFile(path, 'D')
        }, TestContext.CancellationToken);

        var entry = index.Find("dump:/D/linked.bin", TestContext.CancellationToken);

        Assert.IsNotNull(entry);
        Assert.AreEqual(ExtensionDataFixture.DataSize, entry.Value.Size);
        Assert.IsTrue(entry.Value.IsSizeKnown);
    }

    [TestMethod]
    [DataRow(true, ExtensionDataFixture.DataSize, true, DisplayName = "Issue895_VolumeExtensionSize")]
    [DataRow(false, 0L, false, DisplayName = "Issue895_IncludeFreedCannotDonate")]
    public void Volume_ExtensionData_RequiresAllocatedExtension_Issue895(bool inUse, long expectedSize,
        bool expectedKnown)
    {
        var dump = ExtensionDataFixture.Build(options: new ExtensionDataFixture.Options { InUse = inUse });
        using var result = FixtureVolume.Parse(Path.Combine(TemporaryDirectoryPath, "volume.img"), dump, true);

        var entry = result.ToRecords().Single(record => record.RecordNumber == 10);

        Assert.AreEqual(expectedSize, entry.Size);
        Assert.AreEqual(expectedKnown, entry.SizeKnown);
    }
}
