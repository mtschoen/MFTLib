using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.JournalMutatorDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     Journal churn keeps the posting invariant (issue 859, design sections 4.2 to 4.3): after every batch a gather of
///     the base and delta postings, verified against the descriptors, equals a scan of the rows for each name, and a
///     refused delta append surfaces as the journal's capacity-exhausted outcome.
/// </summary>
[TestClass]
public sealed class PostingDeltaJournalTests : IAsyncDisposable
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime ChangeMoment = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);
    static readonly RowColumns DirectoryColumns = new(0, RowFlags.InUse | RowFlags.Directory, 16, 0, Moment.Ticks, 0);

    readonly TempDirectory _directory = new("mftlib-posting-journal");
    BlockFile _block = null!;
    BlockWriter _writer = null!;
    RootBlock[] _snapshot = null!;

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await _snapshot.ReleaseNowAsync();
        _directory.Dispose();
    }

    JournalMutator CreateMutator(uint deltaCapacity)
    {
        _block = BlockFile.Create(TestBlocks.Options(Path.Combine(_directory.Path, "T-0BADF00D.mlix"),
            ProducerKind.Mft, namePoolCapacity: 65536) with
        { DeltaCapacity = deltaCapacity });
        _writer = new BlockWriter(_block);
        _writer.TryWriteRow(0, "", DirectoryColumns);
        _writer.TryWriteRow(1, "Documents", DirectoryColumns with { ParentRow = 0 });
        _writer.TryWriteRow(2, "existing.txt", new RowColumns(1, RowFlags.InUse, 32, 100, Moment.Ticks, 0));
        _writer.Complete(Moment, null);
        _snapshot = [new RootBlock(new VolumeKey('T'), _block, TestRootDirectory.For('T'))];
        return new JournalMutator(_writer);
    }

    IReadOnlyList<FileChange> Apply(JournalMutator mutator, params UsnJournalEntry[] entries) =>
        mutator.Apply(_snapshot[0], entries, journalIdentifier: 7, nextUsn: 2000);

    static UsnJournalEntry Create(ulong record, string name) =>
        CreateEntry(record, 1, name, UsnReason.FileCreate | UsnReason.Close, ChangeMoment);

    static UsnJournalEntry Rename(ulong record, string name) =>
        CreateEntry(record, 1, name, UsnReason.RenameNewName | UsnReason.Close, ChangeMoment);

    static UsnJournalEntry Delete(ulong record, string name) =>
        CreateEntry(record, 1, name, UsnReason.FileDelete | UsnReason.Close, ChangeMoment);

    /// <summary>The rows a posting reader would return for a name: base plus delta, bounded, verified against descriptors.</summary>
    static uint[] Gather(BlockFile block, uint nameId)
    {
        var candidates = new SortedSet<uint>();
        if (nameId <= block.PostingBaseNameCount)
        {
            var offsets = block.PostingOffsets;
            var rows = block.PostingRows;
            for (var slot = offsets[(int)nameId]; slot < offsets[(int)nameId + 1]; slot++)
            {
                candidates.Add(rows[(int)slot]);
            }
        }

        var entries = block.DeltaEntries;
        var count = block.DeltaEntryCount;
        var current = block.DeltaHeads[(int)(nameId & (block.BucketCount - 1))];
        while (current != 0 && current <= count)
        {
            var entry = entries[(int)current];
            if (entry.NameId == nameId)
            {
                candidates.Add(entry.Row);
            }

            current = entry.Next < current ? entry.Next : 0;
        }

        return candidates
            .Where(row => row < block.RowCount &&
                          FileRow.DescriptorNameId(FileRow.ReadDescriptorWord(in block.Rows[(int)row])) == nameId)
            .ToArray();
    }

    static uint[] Scan(BlockFile block, uint nameId)
    {
        var matches = new List<uint>();
        for (var row = 0u; row < block.RowCount; row++)
        {
            if (FileRow.DescriptorNameId(FileRow.ReadDescriptorWord(in block.Rows[(int)row])) == nameId)
            {
                matches.Add(row);
            }
        }

        return matches.ToArray();
    }

    static void AssertEquivalent(BlockFile block)
    {
        Assert.IsNull(SyntheticNamePool.CheckPostingIntegrity(block));
        for (var nameId = 1u; nameId <= block.NameEntryCount; nameId++)
        {
            CollectionAssert.AreEqual(Scan(block, nameId), Gather(block, nameId), $"name {nameId}");
        }
    }

    [TestMethod]
    public void ScriptedChurn_KeepsEveryNamedRowReachable()
    {
        var mutator = CreateMutator(deltaCapacity: 512);

        Apply(mutator, Create(5, "a.txt"), Create(6, "b.txt"));
        AssertEquivalent(_block);
        Apply(mutator, Delete(5, "a.txt"));
        Apply(mutator, Create(5, "a.txt"));
        Apply(mutator, Create(5, "c.txt"));
        AssertEquivalent(_block);
        Apply(mutator, Rename(6, "c.txt"));
        Apply(mutator, Rename(6, "b.txt"));
        Apply(mutator, Rename(6, "B.TXT"));
        Apply(mutator, Rename(2, "renamed.txt"));
        AssertEquivalent(_block);
        Apply(mutator, Rename(2, "existing.txt"));
        AssertEquivalent(_block);
        Assert.IsGreaterThan(0u, _block.DeltaEntryCount);
        Assert.IsFalse(mutator.CapacityExhausted);
    }

    [TestMethod]
    public void RandomizedChurn_KeepsEveryNamedRowReachable()
    {
        var mutator = CreateMutator(deltaCapacity: 20000);
        var random = new Random(859);
        string[] names = ["a", "B", "b", "c.txt", "C.TXT", "d", "index.js", "Index.js", "e", "f"];
        var live = new Dictionary<ulong, string>();
        for (var step = 0; step < 10000; step++)
        {
            var record = (ulong)random.Next(3, 40);
            var name = names[random.Next(names.Length)];
            if (!live.TryGetValue(record, out var current))
            {
                Apply(mutator, Create(record, name));
                live[record] = name;
            }
            else if (random.Next(3) == 0)
            {
                Apply(mutator, Delete(record, current));
                live.Remove(record);
            }
            else
            {
                Apply(mutator, Rename(record, name));
                live[record] = name;
            }

            if (step % 250 == 0)
            {
                AssertEquivalent(_block);
            }
        }

        AssertEquivalent(_block);
        Assert.IsFalse(mutator.CapacityExhausted);
    }

    [TestMethod]
    public void ExhaustedDelta_ReportsCapacityExhaustedKeepsThePriorNameAndKeepsApplyingTheRest()
    {
        var mutator = CreateMutator(deltaCapacity: 1);

        var changes = Apply(mutator, Create(5, "first.txt"), Create(6, "second.txt"), Rename(2, "third.txt"),
            CreateEntry(2, 1, "existing.txt", UsnReason.DataExtend | UsnReason.Close, ChangeMoment));

        Assert.IsTrue(mutator.CapacityExhausted);
        Assert.IsTrue(_block.Header.IsCapacityExhausted);
        Assert.AreEqual(1u, _block.DeltaEntryCount);
        Assert.AreEqual("existing.txt", NamePool.ReadRowName(_block, 2).ToString());
        Assert.IsFalse(_block.Rows[6].IsInUse);
        Assert.IsTrue(changes.Any(change => change.Entry.Name == "first.txt"));
        Assert.IsFalse(changes.Any(change => change.Kind == FileChangeKind.Renamed));
        AssertEquivalent(_block);
    }

    [TestMethod]
    public void InterruptedAppend_EntryWithoutPublishedCount_IsUnreachableAndHarmless()
    {
        CreateMutator(deltaCapacity: 8);
        var nameId = AddNameAndEntry("ghost.txt", row: 2, publishCount: false, publishHead: false);

        AssertEquivalent(_block);
        CollectionAssert.AreEqual(Array.Empty<uint>(), Gather(_block, nameId));
    }

    [TestMethod]
    public void InterruptedAppend_CountWithoutPublishedHead_LeavesQueriesEquivalent()
    {
        CreateMutator(deltaCapacity: 8);
        var nameId = AddNameAndEntry("ghost.txt", row: 2, publishCount: true, publishHead: false);

        CollectionAssert.AreEqual(Scan(_block, nameId), Gather(_block, nameId));
    }

    [TestMethod]
    public void InterruptedAppend_HeadWithoutDescriptor_IsAStaleAssociationVerificationRejects()
    {
        CreateMutator(deltaCapacity: 8);
        var nameId = AddNameAndEntry("ghost.txt", row: 2, publishCount: true, publishHead: true);

        Assert.IsNull(SyntheticNamePool.CheckPostingIntegrity(_block));
        CollectionAssert.AreEqual(Array.Empty<uint>(), Gather(_block, nameId));
        CollectionAssert.AreEqual(Scan(_block, nameId), Gather(_block, nameId));
    }

    [TestMethod]
    public void LostEntryPage_ZeroedEntryEndsTheWalkAndMissesOnlyThatRow()
    {
        var mutator = CreateMutator(deltaCapacity: 8);
        Apply(mutator, Create(5, "a.txt"), Create(6, "b.txt"));
        var lostName = NameIdOf("b.txt");
        var survivingName = NameIdOf("a.txt");
        _block.DeltaEntries[(int)_block.DeltaHeads[(int)(lostName & (_block.BucketCount - 1))]] = default;

        CollectionAssert.AreEqual(Array.Empty<uint>(), Gather(_block, lostName));
        CollectionAssert.AreEqual(new uint[] { 5 }, Gather(_block, survivingName));
    }

    uint NameIdOf(string name)
    {
        Assert.IsTrue(SyntheticNamePool.TryFindOrdinal(_block, name, out var nameId));
        return nameId;
    }

    uint AddNameAndEntry(string name, uint row, bool publishCount, bool publishHead)
    {
        Assert.IsTrue(SyntheticNamePool.TryGetOrAdd(_block, name, out var nameId));
        var heads = _block.DeltaHeads;
        var slot = (int)(nameId & (_block.BucketCount - 1));
        _block.DeltaEntries[1] = new DeltaEntry { Row = row, NameId = nameId, Next = heads[slot] };
        if (publishCount)
        {
            _block.PublishDeltaEntryCount(1);
        }

        if (publishHead)
        {
            heads[slot] = 1;
        }

        return nameId;
    }
}
