using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953: <see cref="FileIndex.Find" /> on a directory view whose lineage replaces its mapping. A lookup
///     walks down from the row its own admission bound on the mapping it pinned, never from the shared binding, which
///     follows the lineage's current mapping. Volume T is mapped to the tree root; the block holds rows 5 (root), 30
///     <c>src</c>, 31 <c>src\a.txt</c>, 32 <c>other</c> and 33 <c>other\b.txt</c>, and the view is <c>src</c>.
/// </summary>
[TestClass]
public sealed class FileIndexDirectoryViewReplacementTests : IndexTreeTestBase
{
    const ulong JournalIdentifier = 7;
    const uint RootRow = 5;
    const uint SourceRow = 30;
    const uint SourceFileRow = 31;
    const uint OtherRow = 32;
    static readonly VolumeKey VolumeT = new('T');
    static readonly DateTime Moment = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    string SourcePath => Path.Combine(TreeRoot, "src");

    string CanonicalPath => Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(VolumeT, UniqueVolumeSerial));

    [TestInitialize]
    public void CreateDirectories()
    {
        Directory.CreateDirectory(SourcePath);
        Directory.CreateDirectory(Path.Combine(TreeRoot, "other"));
    }

    static IEnumerable<SyntheticRow> Rows()
    {
        yield return new SyntheticRow(RootRow, ".", RootRow) { IsDirectory = true };
        yield return new SyntheticRow(SourceRow, "src", RootRow) { IsDirectory = true };
        yield return new SyntheticRow(SourceFileRow, "a.txt", SourceRow) { Size = 10 };
        yield return new SyntheticRow(OtherRow, "other", RootRow) { IsDirectory = true };
        yield return new SyntheticRow(33, "b.txt", OtherRow) { Size = 20 };
    }

    Task<FileIndex> OpenViewAsync() =>
        FileIndex.OpenAsync(SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = RootKey.FromDirectory(SourcePath),
            Producer = ProducerSelection.Automatic,
            CacheDirectory = CacheDirectoryPath,
            MftSource = SyntheticIndexSource.Create(new SyntheticMftProducer(_ => Rows())
            {
                JournalCursor = new SyntheticJournalCursor(JournalIdentifier, 4096),
                ScanTimestamp = Moment
            })
        }, UniqueVolumeSerial, volume: VolumeT, volumeRootDirectory: TreeRoot), TestContext.CancellationToken);

    /// <summary>
    ///     The reviewer's first interleaving: a lookup is admitted at row 30 on the original mapping, then a replacement
    ///     with <c>src</c> renamed away is published and the shared binding resolves absent on it. The lookup still
    ///     answers from the row it was admitted at on the mapping it pinned.
    /// </summary>
    [TestMethod]
    public async Task FindAdmittedBeforeAReplacementLosesTheView_WalksFromTheAdmittedRow()
    {
        await using var index = await OpenViewAsync();
        ReplaceAfterAdmission(index, (SourceRow, "away"));

        var found = index.Find(Path.Combine(SourcePath, "a.txt"), TestContext.CancellationToken);

        Assert.IsFalse(index.CurrentBlock!.View.IsPresent, "the replacement should leave the shared binding absent");
        Assert.AreEqual(SourceFileRow, found?.RecordKey.RecordNumber);
    }

    /// <summary>
    ///     The reviewer's second interleaving: after admission at row 30, the replacement renames <c>src</c> away and
    ///     <c>other</c> (row 32) to <c>src</c>, and the shared binding follows it to row 32. The lookup must not walk the
    ///     pinned original from row 32, which there is the unrelated <c>other</c> directory.
    /// </summary>
    [TestMethod]
    public async Task FindOfTheViewRootAdmittedBeforeAReplacementRebindsTheView_IsTheAdmittedRow()
    {
        await using var index = await OpenViewAsync();
        ReplaceAfterAdmission(index, (SourceRow, "away"), (OtherRow, "src"));

        var viewRoot = index.Find(SourcePath, TestContext.CancellationToken);

        AssertReboundTo(index, OtherRow);
        Assert.AreEqual(SourceRow, viewRoot?.RecordKey.RecordNumber);
    }

    /// <summary>
    ///     The second interleaving below the view root: <c>b.txt</c> exists only under <c>other</c> on the pinned
    ///     original, so a lookup admitted at row 30 finds nothing however the shared binding moved.
    /// </summary>
    [TestMethod]
    public async Task FindBelowTheViewAdmittedBeforeAReplacementRebindsTheView_NeverWalksFromTheNewRow()
    {
        await using var index = await OpenViewAsync();
        ReplaceAfterAdmission(index, (SourceRow, "away"), (OtherRow, "src"));

        var outside = index.Find(Path.Combine(SourcePath, "b.txt"), TestContext.CancellationToken);

        AssertReboundTo(index, OtherRow);
        Assert.IsNull(outside, "the lookup walked the pinned mapping from the row the shared binding moved to");
    }

    static void AssertReboundTo(FileIndex index, uint row)
    {
        Assert.IsTrue(index.CurrentBlock!.View.TryGetRow(out var reboundRow), "the replacement should keep the view present");
        Assert.AreEqual(row, reboundRow);
    }

    /// <summary>
    ///     An absence recorded on a mapping a replacement retired is not trusted for the replacement, which holds the
    ///     directory: a lookup is admitted and resolves the path on the current mapping, as every other query does.
    /// </summary>
    [TestMethod]
    public async Task Find_AfterAnAbsenceRecordedOnARetiredMapping_ResolvesOnTheCurrentMapping()
    {
        await using var index = await OpenViewAsync();
        await index.ApplyJournalEntriesAsync(index.Status.Root,
            [Entry(SourceRow, RootRow, "src2", UsnReason.RenameNewName | UsnReason.Close, 5000)], JournalIdentifier, 5001);
        Assert.IsFalse(index.CurrentBlock!.View.IsPresent);
        Replace(index, (SourceRow, "src"));

        var found = index.Find(Path.Combine(SourcePath, "a.txt"), TestContext.CancellationToken);

        Assert.AreEqual(SourceFileRow, found?.RecordKey.RecordNumber);
    }

    static UsnJournalEntry Entry(ulong record, ulong parent, string name, UsnReason reason, long usn) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = record,
            ParentRecordNumber = parent,
            FileName = name,
            Reason = reason,
            Usn = usn,
            TimestampUtc = Moment,
            FileAttributes = FileAttributes.Directory
        });

    /// <summary>
    ///     Once, after the next query is admitted and before it reads: publishes a replacement with
    ///     <paramref name="renames" /> applied and resolves the shared binding on it, the schedule a growth can
    ///     interleave with any query.
    /// </summary>
    void ReplaceAfterAdmission(FileIndex index, params (uint Row, string Name)[] renames)
    {
        index.Hooks.ReaderAdmitted = () =>
        {
            index.Hooks.ReaderAdmitted = null;
            var rootBlock = index.CurrentBlock!;
            Replace(index, renames);
            rootBlock.View.Resolve(rootBlock, TestContext.CancellationToken);
        };
    }

    /// <summary>
    ///     Stands in for a watch growth: copies the canonical block to a sibling, renames the canonical file aside and
    ///     schedules its deletion on the retired mapping, renames the copy into place and publishes it, then applies
    ///     <paramref name="renames" /> to it. The lineage owns the copy from publication on.
    /// </summary>
    void Replace(FileIndex index, params (uint Row, string Name)[] renames)
    {
        var growingPath = CanonicalPath + ".growing-test";
        var retiredPath = CanonicalPath + ".retired-test-" + Guid.NewGuid().ToString("N");
        File.Copy(CanonicalPath, growingPath);
        File.Move(CanonicalPath, retiredPath);
        File.Move(growingPath, CanonicalPath);
        var rootBlock = index.CurrentBlock!;
        var retired = rootBlock.Mapped.Replace(BlockFile.Open(CanonicalPath, UniqueVolumeSerial, out var validation)
                                               ?? throw new AssertFailedException(
                                                   $"The copied block did not open: {validation}"));
        retired.ReleaseState.ScheduleDeleteAt(retiredPath);
        var writer = new BlockWriter(rootBlock.Block);
        foreach (var (row, name) in renames)
        {
            Assert.AreEqual(WriteRefusal.None, writer.TryRenameRow(row, name, RootRow));
        }
    }
}
