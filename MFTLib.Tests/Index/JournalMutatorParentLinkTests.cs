using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.JournalMutatorDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     A scan-written child row must follow its file to the parent the volume now names after the journal reports
///     the change, including when the old parent directory is then deleted and its record reused by a file
///     (issue 1008). Row 1 is the
///     old parent directory, row 2 the new parent directory, row 3 the child as a scan wrote it under row 1. A
///     hard-link record does not say which link survives, so the source's re-read of the file's record decides.
/// </summary>
[TestClass]
public sealed class JournalMutatorParentLinkTests : IAsyncDisposable
{
    const uint OldParentRow = 1;
    const uint NewParentRow = 2;
    const uint ChildRow = 3;
    const uint AbsentRow = 4;
    const ushort OldParentSequence = 3;
    const ushort ReusedParentSequence = 4;
    const ushort NewParentSequence = 25;
    const ushort ChildSequence = 6;
    const string ChildName = "AppIcon.scale-125.png";

    static readonly DateTime Moment = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime ChangeMoment = new(2026, 11, 18, 6, 0, 0, DateTimeKind.Utc);

    TempDirectory _directory = null!;
    BlockFile _block = null!;
    BlockWriter _writer = null!;
    RootBlock[] _snapshot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = new TempDirectory("mftlib-parent-link");
        _block = TestBlocks.Create(Path.Combine(_directory.Path, "T-000000000BADF00D.mlix"), ProducerKind.Mft, 32);
        _writer = new BlockWriter(_block);
        _writer.TryWriteRow(0, "", Directory(parentRow: 0, sequence: 5));
        _writer.TryWriteRow(OldParentRow, "OldVersion", Directory(parentRow: 0, OldParentSequence));
        _writer.TryWriteRow(NewParentRow, "NewVersion", Directory(parentRow: 0, NewParentSequence));
        _writer.TryWriteRow(ChildRow, ChildName, new RowColumns(OldParentRow, RowFlags.InUse,
            Attributes: 32, Size: 100, Moment.Ticks, ChildSequence));
        _writer.Complete(Moment, null);
        _snapshot = [new RootBlock(new VolumeKey('T'), _block, rootDirectoryPath: TestRootDirectory.For('T'))];
    }

    public async ValueTask DisposeAsync()
    {
        await _snapshot.ReleaseNowAsync();
        _directory.Dispose();
    }

    [TestMethod]
    public void MoveThenOldParentDeletedAndReused_LeavesTheChildUnderItsNewParent()
    {
        Apply([
            Child(OldParentRow, UsnReason.RenameOldName),
            Child(NewParentRow, UsnReason.RenameNewName),
            Child(NewParentRow, UsnReason.RenameNewName | UsnReason.Close),
            .. OldParentDeletedAndReused()
        ]);

        AssertChildUnder(NewParentRow, ChildName);
    }

    [TestMethod]
    public void MoveWithEachRecordInItsOwnBatch_LeavesTheChildUnderItsNewParent()
    {
        Apply([Child(OldParentRow, UsnReason.RenameOldName)]);
        Apply([Child(NewParentRow, UsnReason.RenameNewName)]);
        Apply([Child(NewParentRow, UsnReason.RenameNewName | UsnReason.Close)]);
        foreach (var entry in OldParentDeletedAndReused())
        {
            Apply([entry]);
        }

        AssertChildUnder(NewParentRow, ChildName);
    }

    /// <summary>
    ///     A file with one name gains a second link in the new directory and then loses its link in the old one, as a
    ///     package update does when it hard-links unchanged files from the previous version's directory into the new
    ///     one and then deletes the previous directory. The source reads the file's only remaining name in the new
    ///     directory; the child keeps its sequence number throughout.
    /// </summary>
    [TestMethod]
    public void LinkAddedInNewParentThenOldLinkRemoved_LeavesTheChildUnderItsNewParent()
    {
        var changes = Apply([
            Child(NewParentRow, UsnReason.HardLinkChange),
            Child(NewParentRow, UsnReason.HardLinkChange | UsnReason.Close),
            Child(OldParentRow, UsnReason.HardLinkChange),
            Child(OldParentRow, UsnReason.HardLinkChange | UsnReason.Close),
            .. OldParentDeletedAndReused()
        ], Resolved(NewParentRow, ChildName));

        AssertChildUnder(NewParentRow, ChildName);
        // The changes of the reused parent slot cannot read their entry any more; their paths still say what they were.
        var childChanges = changes.Where(change => Path.GetFileName(change.Path) == ChildName)
            .Select(change => change.Kind).ToArray();
        CollectionAssert.AreEqual(new[] { FileChangeKind.Renamed, FileChangeKind.Modified }, childChanges,
            "the first cycle moves the row once; the second, finding it in place, reports a modification");
        var renamed = changes.First(change => change.Kind == FileChangeKind.Renamed);
        Assert.AreEqual(Path.Combine(TestRootDirectory.For('T'), "OldVersion", ChildName), renamed.PreviousPath);
    }

    [TestMethod]
    [DataRow(unchecked((int)(UsnReason.FileCreate | UsnReason.HardLinkChange | UsnReason.Close)),
        (int)FileChangeKind.Created, DisplayName = "cumulative create and hard-link reasons")]
    [DataRow(unchecked((int)(UsnReason.RenameNewName | UsnReason.HardLinkChange | UsnReason.Close)),
        (int)FileChangeKind.Renamed, DisplayName = "cumulative rename and hard-link reasons")]
    public void ResumedCycleWithCumulativeReasons_AppliesTheResolvedNameAndParent(int reasons, int expectedKind)
    {
        var changes = Apply([
            Child(OldParentRow, (UsnReason)reasons),
            .. OldParentDeletedAndReused()
        ], Resolved(NewParentRow, ChildName));

        AssertChildUnder(NewParentRow, ChildName);
        Assert.AreEqual((FileChangeKind)expectedKind,
            changes.Single(change => Path.GetFileName(change.Path) == ChildName).Kind);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "occupied previous incarnation")]
    [DataRow(true, DisplayName = "deleted previous incarnation")]
    public void CumulativeCreate_ReplacesThePreviousIncarnationWithTheResolvedFile(bool deleted)
    {
        if (deleted)
        {
            Apply([Child(OldParentRow, UsnReason.FileDelete | UsnReason.Close)]);
        }

        const ushort newSequence = ChildSequence + 1;
        const string newName = "new-file.png";
        var changes = Apply([CreateEntry(ChildRow, OldParentRow, ChildName,
                UsnReason.FileCreate | UsnReason.HardLinkChange | UsnReason.Close, ChangeMoment, newSequence)],
            new FileNameResolution(ChildRow, newSequence, FileNameLookupOutcome.Resolved, NewParentRow, newName));

        AssertChildUnder(NewParentRow, newName, newSequence);
        Assert.IsFalse(_block.Rows[(int)ChildRow].SizeKnown);
        Assert.IsFalse(_block.Rows[(int)ChildRow].LastWriteTimeKnown);
        Assert.AreEqual(FileChangeKind.Created, changes.Single().Kind);
    }

    [TestMethod]
    public void RowsOwnLinkRemovedAndAnotherSurvives_TakesTheSurvivingNameAndParent()
    {
        const string survivingName = "AppIcon.png";

        Apply([Child(OldParentRow, UsnReason.HardLinkChange | UsnReason.Close)], Resolved(NewParentRow, survivingName));

        AssertChildUnder(NewParentRow, survivingName);
    }

    [TestMethod]
    public void DifferentLinkRemoved_LeavesTheRowAsItIs()
    {
        var changes = Apply([CreateEntry(ChildRow, NewParentRow, "other-link.png",
            UsnReason.HardLinkChange | UsnReason.Close, ChangeMoment, ChildSequence)], Resolved(OldParentRow, ChildName));

        AssertChildUnder(OldParentRow, ChildName);
        Assert.AreEqual(FileChangeKind.Modified, changes.Single().Kind);
        Assert.IsTrue(_block.Rows[(int)ChildRow].SizeKnown, "a link change keeps the scanned size");
        Assert.IsTrue(_block.Rows[(int)ChildRow].LastWriteTimeKnown, "a link change keeps the scanned last-write time");
    }

    [TestMethod]
    public void LinkAddedOnly_LeavesTheRowAsItIs()
    {
        Apply([
            CreateEntry(ChildRow, NewParentRow, "second-link.png", UsnReason.HardLinkChange, ChangeMoment, ChildSequence),
            CreateEntry(ChildRow, NewParentRow, "second-link.png", UsnReason.HardLinkChange | UsnReason.Close,
                ChangeMoment, ChildSequence)
        ], Resolved(OldParentRow, ChildName));

        AssertChildUnder(OldParentRow, ChildName);
    }

    /// <summary>A record freed, or reused by another file, before the lookup is not applied: the journal's delete is.</summary>
    [TestMethod]
    public void RecordFreedOrReusedBeforeTheLookup_KeepsTheRowUntilItsDelete()
    {
        var superseded = new FileNameResolution(ChildRow, ChildSequence, FileNameLookupOutcome.Superseded, 0, "");

        Apply([Child(OldParentRow, UsnReason.HardLinkChange | UsnReason.Close)], superseded);

        AssertChildUnder(OldParentRow, ChildName);
        Apply([Child(OldParentRow, UsnReason.FileDelete | UsnReason.Close)]);
        Assert.IsTrue(_block.Rows[(int)ChildRow].IsDeleted);
    }

    [TestMethod]
    [DataRow(true, DisplayName = "lookup unreadable")]
    [DataRow(false, DisplayName = "no lookup answer")]
    public void LookupFailed_DetachesTheRowInsteadOfKeepingAnUnverifiedParent(bool answered)
    {
        var unreadable = FileNameResolution.Unreadable(ChildRow, ChildSequence);

        var changes = Apply([
            Child(OldParentRow, UsnReason.HardLinkChange),
            Child(OldParentRow, UsnReason.HardLinkChange | UsnReason.Close)
        ], answered ? [unreadable] : []);

        Assert.AreEqual(BlockLayout.DetachedParentRow, _block.Rows[(int)ChildRow].ParentRow);
        Assert.IsTrue(_block.Rows[(int)ChildRow].IsInUse && !_block.Rows[(int)ChildRow].IsDeleted);
        var change = changes.Single();
        Assert.AreEqual(FileChangeKind.Renamed, change.Kind, "the close record of the cycle changes nothing more");
        Assert.AreEqual(ChildName, change.Path);
        Assert.AreEqual(Path.Combine(TestRootDirectory.For('T'), "OldVersion", ChildName), change.PreviousPath);
    }

    [TestMethod]
    public void LinkChangeOfARowTheIndexNeverHad_WritesTheResolvedNameAndParent()
    {
        var change = Apply([CreateEntry(AbsentRow, OldParentRow, "removed-link.png",
                UsnReason.HardLinkChange | UsnReason.Close, ChangeMoment, sequenceNumber: 2)],
            new FileNameResolution(AbsentRow, 2, FileNameLookupOutcome.Resolved, NewParentRow, "kept.png")).Single();

        Assert.AreEqual(FileChangeKind.Created, change.Kind);
        Assert.AreEqual(NewParentRow, _block.Rows[(int)AbsentRow].ParentRow);
        Assert.AreEqual("kept.png", NamePool.ReadRowName(_block, AbsentRow).ToString());
    }

    static RowColumns Directory(uint parentRow, ushort sequence) =>
        new(parentRow, RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, sequence);

    static UsnJournalEntry Child(uint parentRow, UsnReason reason) =>
        CreateEntry(ChildRow, parentRow, ChildName, reason, ChangeMoment, ChildSequence);

    static FileNameResolution Resolved(uint parentRow, string name) =>
        new(ChildRow, ChildSequence, FileNameLookupOutcome.Resolved, parentRow, name);

    /// <summary>The old parent directory is deleted, then a file reuses its record under the new parent.</summary>
    static UsnJournalEntry[] OldParentDeletedAndReused() =>
    [
        CreateEntry(OldParentRow, 0, "OldVersion", UsnReason.FileDelete | UsnReason.Close, ChangeMoment,
            OldParentSequence, FileAttributes.Directory),
        CreateEntry(OldParentRow, NewParentRow, "servicehub.config.json", UsnReason.FileCreate | UsnReason.Close,
            ChangeMoment, ReusedParentSequence)
    ];

    IReadOnlyList<FileChange> Apply(IReadOnlyList<UsnJournalEntry> entries, params FileNameResolution[] fileNames) =>
        new JournalMutator(_writer, fileNames).ApplyWhole(_snapshot[0], entries, journalIdentifier: 7, nextUsn: 2000);

    void AssertChildUnder(uint parentRow, string name, ushort sequence = ChildSequence)
    {
        ref readonly var child = ref _block.Rows[(int)ChildRow];
        ref readonly var parent = ref _block.Rows[(int)child.ParentRow];
        Assert.IsTrue(child.IsInUse && !child.IsDeleted, "the child is still a live file");
        Assert.AreEqual(sequence, _block.SequenceNumbers[(int)ChildRow]);
        Assert.AreEqual(parentRow, child.ParentRow,
            $"the child names row {child.ParentRow} as its parent, a live {(parent.IsDirectory ? "directory" : "file")}");
        Assert.AreEqual(name, NamePool.ReadRowName(_block, ChildRow).ToString());
    }
}
