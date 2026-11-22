using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Issue 998: a base record whose only Win32 or POSIX $FILE_NAME lives in an extension record must still
// get a row, or every row under it is left without a parent.
public partial class DumpParseNativeTests
{
    [TestMethod]
    [DataRow(24, false, 32u, 1u, DisplayName = "Issue998_ExtensionAfterBase")]
    [DataRow(8, false, 32u, 1u, DisplayName = "Issue998_ExtensionBeforeBase")]
    [DataRow(24, true, 32u, 1u, DisplayName = "Issue998_BaseKeepsOnlyDosName")]
    [DataRow(24, false, 8u, 1u, DisplayName = "Issue998_AcrossReadsSerial")]
    [DataRow(8, false, 8u, 2u, DisplayName = "Issue998_BeforeBaseAcrossReadsParallel")]
    [DataRow(24, false, 32u, 4u, DisplayName = "Issue998_AcrossSlicesParallel")]
    [TestCategory("Smoke")]
    public void Parse_DirectoryNamedOnlyInExtension_IsEmitted(int extensionRecordNumber, bool baseKeepsDosName,
        uint bufferSizeRecords, uint threadCount)
    {
        var path = MftDumpFixture.WriteFile(TemporaryDirectoryPath, ExtensionNameFixture.Build(
            new ExtensionNameFixture.Options
            {
                ExtensionRecordNumber = extensionRecordNumber,
                BaseKeepsDosName = baseKeepsDosName
            }));
        NativeTestHooks.NativeSetMaxThreads(threadCount);

        var records = DirectParse.ParseFile(path, out _, bufferSizeRecords);

        var directory = records.Single(record => record.RecordNumber == ExtensionNameFixture.BaseRecordNumber);
        Assert.AreEqual(ExtensionNameFixture.DirectoryName, directory.FileName);
        Assert.AreEqual(5UL, directory.ParentRecordNumber);
        Assert.IsTrue(directory.IsDirectory);
        Assert.IsTrue(directory.InUse);
        Assert.AreEqual(ExtensionNameFixture.BaseSequenceNumber, directory.SequenceNumber);
        Assert.AreEqual(FileAttributes.Directory, directory.FileAttributes);
        Assert.AreEqual(MftDumpFixture.BaseFileTime, directory.ModifiedFileTime);
        Assert.DoesNotContain(record => record.RecordNumber == (ulong)extensionRecordNumber, records);
        CollectionAssert.AreEqual(records.Select(record => record.RecordNumber).Order().ToArray(),
            records.Select(record => record.RecordNumber).ToArray(), "Rows stay in record order");
    }

    [TestMethod]
    [DataRow("sequence", DisplayName = "Issue998_ReusedBaseGetsNoName")]
    [DataRow("freed", DisplayName = "Issue998_FreedExtensionGetsNoName")]
    [DataRow("dos", DisplayName = "Issue998_DosOnlyExtensionGetsNoName")]
    [DataRow("no-list", DisplayName = "Issue998_BaseWithoutAttributeListGetsNoName")]
    public void Parse_IneligibleExtensionName_LeavesTheBaseOut(string scenario)
    {
        var options = scenario switch
        {
            "sequence" => new ExtensionNameFixture.Options { ReferencedSequenceNumber = 4 },
            "freed" => new ExtensionNameFixture.Options { ExtensionInUse = false },
            "dos" => new ExtensionNameFixture.Options { ExtensionNamespace = 2 },
            "no-list" => new ExtensionNameFixture.Options { BaseHasAttributeList = false },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var path = MftDumpFixture.WriteFile(TemporaryDirectoryPath, ExtensionNameFixture.Build(options));

        var records = DirectParse.ParseFile(path, out _, 8);

        Assert.DoesNotContain(record => record.RecordNumber == ExtensionNameFixture.BaseRecordNumber, records);
    }

    [TestMethod]
    [DataRow(20, "Second", DisplayName = "Issue998_LowerExtensionNameWins")]
    [DataRow(28, ExtensionNameFixture.DirectoryName, DisplayName = "Issue998_HigherExtensionNameLoses")]
    public void Parse_SeveralExtensionNames_TakeTheLowestExtensionRecord(int secondExtensionRecordNumber,
        string expectedName)
    {
        var path = MftDumpFixture.WriteFile(TemporaryDirectoryPath, ExtensionNameFixture.Build(
            new ExtensionNameFixture.Options { SecondExtensionRecordNumber = secondExtensionRecordNumber }));
        NativeTestHooks.NativeSetMaxThreads(2);

        var records = DirectParse.ParseFile(path, out _, 8);

        Assert.AreEqual(expectedName,
            records.Single(record => record.RecordNumber == ExtensionNameFixture.BaseRecordNumber).FileName);
    }

    [TestMethod]
    public void Parse_FileNamedOnlyInExtension_KeepsItsSizeUnknown_Issue998()
    {
        var path = MftDumpFixture.WriteFile(TemporaryDirectoryPath,
            ExtensionNameFixture.Build(new ExtensionNameFixture.Options { File = true }));

        var records = DirectParse.ParseFile(path, out _, 8);

        var file = records.Single(record => record.RecordNumber == ExtensionNameFixture.BaseRecordNumber);
        Assert.AreEqual(ExtensionNameFixture.DirectoryName, file.FileName);
        Assert.IsFalse(file.IsDirectory);
        Assert.IsFalse(file.SizeKnown);
    }

    [TestMethod]
    [DataRow(1u, DisplayName = "Issue998_StreamedSerial")]
    [DataRow(4u, DisplayName = "Issue998_StreamedParallel")]
    public async Task FileIndex_ChildOfDirectoryNamedOnlyInExtension_IsRooted_Issue998(uint threadCount)
    {
        var path = MftDumpFixture.WriteFile(TemporaryDirectoryPath, ExtensionNameFixture.Build());
        NativeTestHooks.NativeSetMaxThreads(threadCount);
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            MftSource = MftIndexSources.FromMftDumpFile(path, 'D')
        }, TestContext.CancellationToken);

        var child = index.Find("dump:/D/Spilled/inside.txt", TestContext.CancellationToken);

        Assert.IsNotNull(child);
        Assert.AreEqual(5L, child.Value.Size);
    }

    [TestMethod]
    public void Volume_DirectoryNamedOnlyInExtension_IsEmitted_Issue998()
    {
        using var result = FixtureVolume.Parse(Path.Combine(TemporaryDirectoryPath, "volume.img"),
            ExtensionNameFixture.Build(), false);

        var directory = result.ToRecords().Single(record => record.RecordNumber == ExtensionNameFixture.BaseRecordNumber);

        Assert.AreEqual(ExtensionNameFixture.DirectoryName, directory.FileName);
        Assert.IsTrue(directory.IsDirectory);
    }
}
