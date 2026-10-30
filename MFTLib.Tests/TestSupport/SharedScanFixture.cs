using MFTLib.Index;
using MFTLib.Tests.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

sealed class SharedScanFixture : IDisposable
{
    readonly IndexTreeFixture _tree = new(seedDocuments: false);
    readonly uint _serial = TestVolumeSerial.GetNext();
    readonly SyntheticMftProducer _producer;
    TestGate? _productionGate;

    internal SharedScanFixture(ScriptedWatchSource? watches = null)
    {
        Directory.CreateDirectory(Path.Combine(SourcePath, "deeper"));
        Directory.CreateDirectory(WorkPath);
        _producer = new SyntheticMftProducer(_ => Rows())
        {
            JournalCursor = new SyntheticJournalCursor(7, 4096),
            ScanTimestamp = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc),
            CatchUpLoss = _ => CatchUpLoss?.Invoke(),
            BeforeProduceAsync = async (_, cancellationToken) =>
            {
                LastProductionToken = cancellationToken;
                BeforeProduction?.Invoke();
                if (Volatile.Read(ref _productionGate) is { } gate)
                {
                    gate.MarkEntered();
                    await gate.WaitForReleaseAsync(cancellationToken);
                }
            }
        };
        Source = SyntheticIndexSource.Create(_producer, watches);
    }

    internal VolumeScanRegistry Registry { get; } = new();
    internal MftIndexSource Source { get; }
    internal VolumeKey Volume { get; } = new('T');
    internal string TreeRoot => _tree.TreeRoot;
    internal string SourcePath => Path.Combine(TreeRoot, "src");
    internal string WorkPath => Path.Combine(TreeRoot, "work");
    internal string CacheDirectoryPath => _tree.CacheDirectory;
    internal int ProductionCount => _producer.ProducedVolumes.Count;
    internal CancellationToken LastProductionToken { get; private set; }
    internal Action? BeforeProduction { get; set; }
    internal Func<JournalCheckpointLoss?>? CatchUpLoss { get; set; }

    internal TestGate HoldProduction()
    {
        var gate = new TestGate();
        Volatile.Write(ref _productionGate, gate);
        return gate;
    }

    internal FileIndexOptions Options(RootKey root, CacheMode? mode = null, CacheTag tag = default) =>
        SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = root,
            CacheMode = mode,
            CacheTag = tag,
            CacheDirectory = CacheDirectoryPath,
            MftSource = Source,
            ScanRegistry = Registry
        }, _serial, volume: Volume, volumeRootDirectory: TreeRoot);

    internal Task<FileIndex> OpenAsync(RootKey root, CancellationToken cancellationToken = default) =>
        FileIndex.OpenAsync(Options(root), cancellationToken);

    internal static Task<IReadOnlyList<FileChange>> ApplyAsync(FileIndex index, params UsnJournalEntry[] entries) =>
        Task.Run(() => index.ApplyJournalEntriesAsync(new VolumeKey('T'), entries, 7, 5001));

    internal static UsnJournalEntry Record(ulong row, ulong parent, string name, UsnReason reason,
        FileAttributes attributes = FileAttributes.Normal) => UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = row,
            ParentRecordNumber = parent,
            FileName = name,
            Reason = reason | UsnReason.Close,
            FileAttributes = attributes,
            Usn = 5000,
            TimestampUtc = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc)
        });

    static IEnumerable<SyntheticRow> Rows() =>
    [
        new(5, ".", 5) { IsDirectory = true },
        new(30, "src", 5) { IsDirectory = true },
        new(31, "a.txt", 30) { Size = 10 },
        new(32, "work", 5) { IsDirectory = true },
        new(33, "b.txt", 32) { Size = 20 },
        new(34, "deeper", 30) { IsDirectory = true },
        new(35, "c.txt", 34) { Size = 30 }
    ];

    public void Dispose()
    {
        Volatile.Read(ref _productionGate)?.Release();
        _tree.Dispose();
    }
}
