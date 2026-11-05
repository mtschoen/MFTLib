using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.JournalMutatorDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     Journal apply over a directory tree deeper than the old 128-hop path cap: NTFS has no depth limit below its
///     path length cap, so a legitimately deep tree must apply, publish every change and advance the cursor.
/// </summary>
[TestClass]
public sealed class JournalMutatorDeepTreeTests : IAsyncDisposable
{
    const int NestedDirectoryCount = 140;
    const long BatchNextUsn = 5000;

    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime ChangeMoment = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    TempDirectory _directory = null!;
    BlockFile _block = null!;
    BlockWriter _writer = null!;
    RootBlock[] _snapshot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = new TempDirectory("mftlib-deep-tree");
        _block = TestBlocks.Create(Path.Combine(_directory.Path, "T-0BADF00D.mlix"), ProducerKind.Mft,
            slotCapacity: 256, namePoolCapacity: 4096);
        _writer = new BlockWriter(_block);
        _writer.TryWriteRow(0, "", new RowColumns(ParentRow: 0,
            RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 0));
        _writer.Complete(Moment, null);
        _snapshot = [new RootBlock(new VolumeKey('T'), _block, rootDirectoryPath: TestRootDirectory.For('T'))];
    }

    public async ValueTask DisposeAsync()
    {
        await _snapshot.ReleaseNowAsync();
        _directory.Dispose();
    }

    [TestMethod]
    public void Apply_CreatesOfANestedTreeDeeperThan128Levels_PublishesEveryChangeAndAdvancesTheCursor()
    {
        var entries = new List<UsnJournalEntry>();
        for (var level = 1; level <= NestedDirectoryCount; level++)
        {
            entries.Add(CreateEntry((ulong)level, (ulong)(level - 1), "d", UsnReason.FileCreate | UsnReason.Close,
                ChangeMoment, fileAttributes: FileAttributes.Directory));
        }

        const ulong shallowFileRecord = NestedDirectoryCount + 1;
        entries.Add(CreateEntry(shallowFileRecord, 0, "shallow.txt", UsnReason.FileCreate | UsnReason.Close,
            ChangeMoment));

        var changes = new JournalMutator(_writer).Apply(_snapshot[0], entries, journalIdentifier: 7,
            nextUsn: BatchNextUsn);

        Assert.HasCount(NestedDirectoryCount + 1, changes);
        Assert.IsTrue(changes.All(change => change.Kind == FileChangeKind.Created));
        Assert.IsTrue(changes.All(change => !string.IsNullOrEmpty(change.Path)));
        var deepestTail = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("d", NestedDirectoryCount));
        Assert.EndsWith(deepestTail, changes[NestedDirectoryCount - 1].Path);
        Assert.StartsWith(TestRootDirectory.For('T').TrimEnd(Path.DirectorySeparatorChar),
            changes[NestedDirectoryCount - 1].Path);
        Assert.AreEqual("shallow.txt", changes[NestedDirectoryCount].Entry.Name);
        Assert.AreEqual(BatchNextUsn, _block.Header.UsnNextUsn);
        Assert.AreEqual(7UL, _block.Header.UsnJournalIdentifier);
    }

    [TestMethod]
    public async Task Apply_UnderAChainPastTheDepthCap_DegradesThatChangeToItsNameAndCommitsTheBatch()
    {
        using var directory = new TempDirectory("mftlib-past-cap");
        // Releasing the snapshot releases its block.
        RootBlock[] snapshot =
        [
            new RootBlock(new VolumeKey('T'),
                TestBlocks.Create(Path.Combine(directory.Path, "T-0BADF00D.mlix"), ProducerKind.Mft,
                    slotCapacity: BlockLayout.MaximumPathDepth + 64, namePoolCapacity: 4096),
                rootDirectoryPath: TestRootDirectory.For('T'))
        ];
        try
        {
            var block = snapshot[0].Block;
            var writer = new BlockWriter(block);
            var directoryColumns = new RowColumns(ParentRow: 0,
                RowFlags.InUse | RowFlags.Directory, Attributes: 16, Size: 0, Moment.Ticks, SequenceNumber: 0);
            writer.TryWriteRow(0, "", directoryColumns);
            for (var level = 1u; level <= BlockLayout.MaximumPathDepth; level++)
            {
                writer.TryWriteRow(level, "d", directoryColumns with { ParentRow = level - 1 });
            }

            writer.Complete(Moment, null);
            const ulong deepFileRecord = BlockLayout.MaximumPathDepth + 1;
            const ulong shallowFileRecord = deepFileRecord + 1;
            var changes = new JournalMutator(writer).Apply(snapshot[0],
            [
                CreateEntry(deepFileRecord, BlockLayout.MaximumPathDepth, "past-the-cap.txt",
                    UsnReason.FileCreate | UsnReason.Close, ChangeMoment),
                CreateEntry(shallowFileRecord, 0, "shallow.txt", UsnReason.FileCreate | UsnReason.Close, ChangeMoment)
            ], journalIdentifier: 7, nextUsn: BatchNextUsn);

            Assert.HasCount(2, changes);
            Assert.AreEqual("past-the-cap.txt", changes[0].Path);
            Assert.AreEqual(Path.Combine(TestRootDirectory.For('T'), "shallow.txt"), changes[1].Path);
            Assert.AreEqual(BatchNextUsn, block.Header.UsnNextUsn);
            Assert.ThrowsExactly<InvalidDataException>(() => changes[0].Entry.Path);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }
}
