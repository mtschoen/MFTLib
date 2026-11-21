using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     One MFT index over a synthetic volume <c>T</c> whose canonical cache block has small explicit capacities, so a
///     test can drive one region to its limit and watch the block grow. Rows: 5 the root, 6 <c>a.txt</c>, 7 the
///     directory <c>docs</c> and 8 <c>docs\b.txt</c>. The scripted watch source resumes from <see cref="StartUsn" />.
///     It collects every change, diagnostics line and fault.
/// </summary>
sealed class WatchGrowthFixture : IAsyncDisposable
{
    public const ulong JournalIdentifier = 7;
    public const long StartUsn = 4096;
    public const uint RootRow = 5;
    public const uint FileRow = 6;
    public const uint DirectoryRow = 7;
    public const uint NestedFileRow = 8;
    public static readonly VolumeKey Volume = new('T');
    public static readonly DateTime Moment = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>64 slots, 64 name entries, a 4096-byte pool and a 16-entry delta.</summary>
    public static readonly BlockCapacities SmallCapacities = new(64, 64, 4096, 16);

    readonly ConcurrentQueue<string> _diagnostics;
    readonly ConcurrentQueue<FileChange> _changes = [];
    readonly ConcurrentQueue<WatchFault> _faults = [];

    WatchGrowthFixture(string cacheDirectory, ulong volumeSerial, FileIndexOptions options, FileIndex index,
        ScriptedWatchSource watches, ConcurrentQueue<string> diagnostics)
    {
        _diagnostics = diagnostics;
        Options = options;
        CacheDirectory = cacheDirectory;
        VolumeSerial = volumeSerial;
        Index = index;
        Watches = watches;
        index.Changed += OnChanged;
        index.WatchFaulted += _faults.Enqueue;
    }

    readonly List<(int Count, TaskCompletionSource Reached)> _changeWaiters = [];

    void OnChanged(FileChange change)
    {
        lock (_changeWaiters)
        {
            _changes.Enqueue(change);
            foreach (var waiter in _changeWaiters.Where(waiter => waiter.Count <= _changes.Count).ToArray())
            {
                waiter.Reached.TrySetResult();
                _changeWaiters.Remove(waiter);
            }
        }
    }

    /// <summary>Completes once <paramref name="count" /> changes in all have been raised.</summary>
    public Task WaitForChangeCountAsync(int count)
    {
        lock (_changeWaiters)
        {
            if (_changes.Count >= count)
            {
                return Task.CompletedTask;
            }

            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _changeWaiters.Add((count, reached));
            return reached.Task.WaitAsync(TestTimeouts.HangGuard);
        }
    }

    /// <summary>Starts the root's watch and returns the scripted handle of start <paramref name="startNumber" />.</summary>
    public async Task<ScriptedVolumeWatch> StartWatchingAsync(int startNumber = 1)
    {
        var result = await Index.StartWatchingAsync(CancellationToken.None);
        if (result.Outcome != RootOperationOutcome.Succeeded)
        {
            throw new InvalidOperationException($"The watch did not start: {result.Outcome} {result.Failure?.Message}");
        }

        return await Watches.WaitForStartAsync(Volume, startNumber, CancellationToken.None)
            .WaitAsync(TestTimeouts.HangGuard);
    }

    /// <summary>
    ///     Copies every file of <paramref name="cacheDirectory" /> except the owner lock into <paramref name="directory" />: what the disk holds at this
    ///     instant, as a process killed here would leave it. A block's mapped pages are read through the page cache.
    /// </summary>
    public static void SnapshotCacheTo(string cacheDirectory, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.GetFiles(cacheDirectory))
        {
            if (path.EndsWith(".lock", StringComparison.Ordinal))
            {
                continue;
            }

            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = new FileStream(Path.Combine(directory, Path.GetFileName(path)), FileMode.CreateNew);
            source.CopyTo(target);
        }
    }

    public FileIndex Index { get; }

    /// <summary>The options the index was opened with, for a test that opens another index over the same source.</summary>
    public FileIndexOptions Options { get; }
    public ScriptedWatchSource Watches { get; }
    public string CacheDirectory { get; }
    public ulong VolumeSerial { get; }
    public IReadOnlyCollection<FileChange> Changes
    {
        get
        {
            lock (_changeWaiters)
            {
                return [.. _changes];
            }
        }
    }
    public IReadOnlyCollection<WatchFault> Faults => _faults;
    public string CanonicalPath => CanonicalPathIn(CacheDirectory, VolumeSerial);
    public RootBlock RootBlock => Index.CurrentBlock ?? throw new InvalidOperationException("The root has no block.");
    public BlockFile Block => RootBlock.Block;

    public static string CanonicalPathIn(string cacheDirectory, ulong volumeSerial) =>
        Path.Combine(cacheDirectory, MFTLib.Index.CacheDirectory.BlockFileName(Volume, volumeSerial));

    public static IEnumerable<SyntheticRow> Rows()
    {
        yield return new SyntheticRow(RootRow, ".", RootRow) { IsDirectory = true };
        yield return new SyntheticRow(FileRow, "a.txt", RootRow) { Size = 10 };
        yield return new SyntheticRow(DirectoryRow, "docs", RootRow) { IsDirectory = true };
        yield return new SyntheticRow(NestedFileRow, "b.txt", DirectoryRow) { Size = 20 };
    }

    /// <summary>
    ///     Opens the index. <paramref name="beforeProduce" /> runs before every production after the open's, so a test
    ///     can hold a rescan in production.
    /// </summary>
    public static async Task<WatchGrowthFixture> OpenAsync(string cacheDirectory, string treeRoot, ulong volumeSerial,
        BlockCapacities? capacities = null, Func<CancellationToken, Task>? beforeProduce = null,
        CacheMode cacheMode = CacheMode.Persistent)
    {
        var diagnostics = new ConcurrentQueue<string>();
        var watches = new ScriptedWatchSource();
        var productions = 0;
        var producer = new SyntheticMftProducer(_ => Rows())
        {
            JournalCursor = new SyntheticJournalCursor(JournalIdentifier, StartUsn),
            ScanTimestamp = Moment,
            Capacities = capacities ?? SmallCapacities,
            BeforeProduceAsync = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref productions) > 1 && beforeProduce is not null)
                {
                    await beforeProduce(cancellationToken);
                }
            }
        };
        var options = SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = Volume,
            Producer = ProducerSelection.Automatic,
            CacheDirectory = cacheMode == CacheMode.Disabled ? null : cacheDirectory,
            CacheMode = cacheMode,
            MftSource = SyntheticIndexSource.Create(producer, watches),
            Diagnostics = diagnostics.Enqueue
        }, volumeSerial, volume: Volume, volumeRootDirectory: treeRoot);
        var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        return new WatchGrowthFixture(cacheDirectory, volumeSerial, options, index, watches, diagnostics);
    }

    /// <summary>The diagnostics lines the index sent, in order.</summary>
    public IReadOnlyList<string> DiagnosticsLines => [.. _diagnostics];

    /// <summary>The lines that announce a growth.</summary>
    public IReadOnlyList<string> GrowthLines => [.. DiagnosticsLines.Where(line => line.StartsWith("Grew block file", StringComparison.Ordinal))];

    /// <summary>Applies <paramref name="entries" /> as one batch through the index, ending at <paramref name="nextUsn" />.</summary>
    public Task<IReadOnlyList<FileChange>> ApplyAsync(long nextUsn, params UsnJournalEntry[] entries) =>
        Index.ApplyJournalEntriesAsync(Volume, entries, JournalIdentifier, nextUsn);

    public static UsnJournalEntry Create(ulong record, string name, long usn, ulong parent = RootRow) =>
        Entry(record, parent, name, UsnReason.FileCreate | UsnReason.Close, usn);

    public static UsnJournalEntry Rename(ulong record, string name, long usn, ulong parent = RootRow) =>
        Entry(record, parent, name, UsnReason.RenameNewName | UsnReason.Close, usn);

    public static UsnJournalEntry Entry(ulong record, ulong parent, string name, UsnReason reason, long usn,
        FileAttributes attributes = FileAttributes.Archive) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = record,
            ParentRecordNumber = parent,
            FileName = name,
            Reason = reason,
            Usn = usn,
            SequenceNumber = 1,
            TimestampUtc = Moment,
            FileAttributes = attributes
        });

    /// <summary>The scripted record of <see cref="Create" />, for a batch delivered through a running watch.</summary>
    public static SyntheticJournalRecord CreateRecord(ulong record, string name, long usn, ulong parent = RootRow) =>
        new()
        {
            RecordNumber = record,
            ParentRecordNumber = parent,
            FileName = name,
            Usn = usn,
            SequenceNumber = 1,
            Reason = SyntheticJournalReason.FileCreate | SyntheticJournalReason.Close,
            FileAttributes = FileAttributes.Archive,
            Timestamp = Moment
        };

    public ValueTask DisposeAsync() => Index.DisposeAsync();
}
