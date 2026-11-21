using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.ReadInlineContentAsync" /> on a directory root, which is a view of the whole-volume block:
///     a row index is still the MFT record number, so the read asks the volume for the same records, and a view whose
///     directory was moved away holds no block, so its old entries name nothing.
///     Volume T is mapped to the tree root; the block holds rows 5 (root), 30 <c>src</c>, 31 <c>src\a.txt</c>
///     (sequence 3), 32 <c>other</c> and 33 <c>other\b.txt</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexInlineContentViewTests : IndexTreeTestBase
{
    const ulong JournalIdentifier = 7;
    const uint SourceRow = 30;
    static readonly VolumeKey VolumeT = new('T');
    static readonly DateTime Moment = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    string SourcePath => Path.Combine(TreeRoot, "src");

    [TestInitialize]
    public void CreateDirectories()
    {
        Directory.CreateDirectory(SourcePath);
        Directory.CreateDirectory(Path.Combine(TreeRoot, "other"));
    }

    [TestCleanup]
    public void ResetJournalSettings() => UsnJournalSettingsQuery.Dependencies.ResetToDefaults();

    static IEnumerable<SyntheticRow> Rows()
    {
        yield return new SyntheticRow(5, ".", 5) { IsDirectory = true };
        yield return new SyntheticRow(SourceRow, "src", 5) { IsDirectory = true };
        yield return new SyntheticRow(31, "a.txt", SourceRow) { Size = 10, SequenceNumber = 3 };
        yield return new SyntheticRow(32, "other", 5) { IsDirectory = true };
        yield return new SyntheticRow(33, "b.txt", 32) { Size = 20, SequenceNumber = 4 };
    }

    Task<FileIndex> OpenViewAsync(ScriptedInlineContentReader reader)
    {
        var producer = new SyntheticMftProducer(_ => Rows())
        {
            JournalCursor = new SyntheticJournalCursor(JournalIdentifier, 4096),
            ScanTimestamp = Moment
        };
        var source = new MftIndexSource(producer.Producer, inlineContentReader: reader);
        return FileIndex.OpenAsync(SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = RootKey.FromDirectory(SourcePath),
            Producer = ProducerSelection.Automatic,
            CacheDirectory = CacheDirectoryPath,
            MftSource = source
        }, UniqueVolumeSerial, volume: VolumeT, volumeRootDirectory: TreeRoot), TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Read_ThroughADirectoryView_AsksTheVolumeForTheRecordsOfItsRows()
    {
        var reader = new ScriptedInlineContentReader();
        await using var index = await OpenViewAsync(reader);
        var entry = index.Find(Path.Combine(SourcePath, "a.txt"), TestContext.CancellationToken)!.Value;
        var contents = new List<byte[]>();

        var result = await index.ReadInlineContentAsync([entry], answer => contents.Add(answer.Content.ToArray()),
            TestContext.CancellationToken);

        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome, result.Failure?.ToString());
        Assert.AreEqual(index.Status.Root, result.Root);
        CollectionAssert.AreEqual(new[] { new InlineContentRequest(0, 31, 3) }, Assert.ContainsSingle(reader.Calls),
            "the row index of a view is the record number in the volume's MFT");
        Assert.AreEqual(VolumeT, Assert.ContainsSingle(reader.Volumes));
        CollectionAssert.AreEqual(BitConverter.GetBytes(31L), Assert.ContainsSingle(contents));
    }

    [TestMethod]
    public async Task Read_ViewWhoseDirectoryWasMovedAway_ReadsAnOldEntryByItsRecordNotByTheViewsMembership()
    {
        var reader = new ScriptedInlineContentReader();
        await using var index = await OpenViewAsync(reader);
        var entry = index.Find(Path.Combine(SourcePath, "a.txt"), TestContext.CancellationToken)!.Value;
        await index.ApplyJournalEntriesAsync(index.Status.Root,
        [
            UsnJournalEntry.Create(new UsnJournalEntryOptions
            {
                RecordNumber = SourceRow,
                ParentRecordNumber = 5,
                FileName = "src2",
                Reason = UsnReason.RenameNewName | UsnReason.Close,
                Usn = 5000,
                TimestampUtc = Moment,
                FileAttributes = FileAttributes.Directory
            })
        ], JournalIdentifier, 5001);
        Assert.AreEqual(RootState.Offline, index.Status.State);
        Assert.IsNull(index.Status.Block);

        var emptyRead = await index.ReadInlineContentAsync([], _ => { }, TestContext.CancellationToken);
        var outcomes = new List<InlineContentOutcome>();
        var oldEntryRead = await index.ReadInlineContentAsync([entry], answer => outcomes.Add(answer.Outcome),
            TestContext.CancellationToken);

        Assert.AreEqual(RootOperationOutcome.Succeeded, emptyRead.Outcome);
        Assert.AreEqual(RootOperationOutcome.Succeeded, oldEntryRead.Outcome);
        Assert.AreEqual(InlineContentOutcome.Returned, Assert.ContainsSingle(outcomes),
            "an entry names a record and sequence number of the volume's block, which the view's move does not change");
        CollectionAssert.AreEqual(new[] { new InlineContentRequest(0, 31, 3) }, Assert.ContainsSingle(reader.Calls));
    }
}
