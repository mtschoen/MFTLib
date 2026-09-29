using MFTLib.Index;
using MFTLib.Tests.TestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     Seeds a cache block for drive <c>T</c> while the journal still holds its checkpoint, then
///     opens it cache-only after the journal has trimmed that checkpoint away, which is how a
///     cache-only open comes to adopt an unresumable block. The journal read is swapped out
///     through <c>JournalCheckpointCheck.OverrideJournalForTest</c>, a process-wide override, so a
///     class using this carries <c>[DoNotParallelize]</c>.
/// </summary>
internal sealed class CacheOnlyUnresumableFixture : IDisposable
{
    const ulong CachedJournalId = 0xABCD;
    const long CachedNextUsn = 1_000_000;
    static readonly DateTime FixedMoment = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    readonly string _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
    readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");

    public CacheOnlyUnresumableFixture()
    {
        Directory.CreateDirectory(_treeRoot);
    }

    public FakeIndexWatchSource Source { get; } = new();

    public async Task<FileIndex> OpenAdoptingAnUnresumableBlockAsync(CancellationToken cancellationToken)
    {
        using (Journal(firstUsn: 0))
        {
            await using var seeding = await FileIndex.OpenAsync(Options(cacheOnly: false), cancellationToken);
        }

        using (Journal(firstUsn: CachedNextUsn + 500))
        {
            return await FileIndex.OpenAsync(Options(cacheOnly: true), cancellationToken);
        }
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _treeRoot, _cacheDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows.
            }
        }
    }

    FileIndexOptions Options(bool cacheOnly)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = ProduceMftShapedBlock,
            WatchSource = Source,
            InitialOpenCacheOnly = cacheOnly
        };
    }

    static IDisposable Journal(long firstUsn)
    {
        return JournalCheckpointCheck.OverrideJournalForTest(_ =>
            new JournalWindow(CachedJournalId, firstUsn, CachedNextUsn + 4_000, 64, 128L * 1024 * 1024));
    }

    /// <summary>An MFT-kind block carrying the checkpoint a warm start would resume from.</summary>
    static Task<MftBlockProduceResult> ProduceMftShapedBlock(
        MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        var createOptions = new BlockFileCreateOptions
        {
            Path = request.BlockPath,
            VolumeSerial = request.VolumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(8),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(256),
            DeleteOnClose = request.DeleteOnClose
        };

        using (var block = BlockFile.Create(createOptions))
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "$MFT",
                new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 0, Size: 0,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
            writer.TryWriteRow(5, ".",
                new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0, Size: 0,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
            writer.SetJournalCursor(CachedJournalId, CachedNextUsn);
            writer.Complete(FixedMoment);
        }

        return Task.FromResult(new MftBlockProduceResult(
            BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
            CachedJournalId, CachedNextUsn, SkippedRecordCount: 0, CompactionNeeded: false));
    }
}
