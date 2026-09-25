using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

internal sealed class InProcessBlockBrokerHarness : IAsyncDisposable
{
    internal static readonly UsnJournalCursor ArmedCursor = new(71, 12345);
    readonly Stream _server;
    readonly RecordingBlockSectionWriter _writer;
    readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(20));
    readonly Task _serving;
    readonly Dictionary<string, BlockFile> _sections = new();

    public sealed class Options
    {
        public Action<BlockFile>? ChangeHeader { get; set; }
        public bool SourceFails { get; set; }
        public MftRecordBatchSource? RecordBatches { get; set; }
        public UsnJournalCatchUpSource? CatchUp { get; set; }
        public NtfsVolumeInformation? VolumeInformation { get; set; }
        public UsnJournalCursorQuery? QueryCursor { get; set; }
        public JournalBatchSource? WatchDrive { get; set; }

        /// <summary>Wraps the client's end of the pipe, so a test can gate or observe its frames.</summary>
        public Func<Stream, Stream>? WrapClientTransport { get; set; }
    }

    public InProcessBlockBrokerHarness(Options options)
    {
        var (clientTransport, server) = DuplexStream.CreatePair();
        _server = server;
        _writer = new RecordingBlockSectionWriter(name => _sections[name]);
        Client = new JournalBrokerClient(options.WrapClientTransport?.Invoke(clientTransport) ?? clientTransport,
            (_, blockOptions) =>
            {
                CreatedBlock = BlockFile.Create(blockOptions);
                SectionName = $"test-section-{Guid.NewGuid():N}";
                Lifetime = new CountingLifetime();
                _sections.Add(SectionName, CreatedBlock);
                return (SectionName, CreatedBlock, Lifetime);
            });
        var host = new JournalBrokerHost(options.QueryCursor ?? (_ => ArmedCursor),
            readJournal: (drive, cursor) =>
            {
                options.ChangeHeader?.Invoke(CreatedBlock!);
                return options.CatchUp == null
                    ? ([JournalEntryFactory.Create(20, 12400, "file.txt")], new UsnJournalCursor(71, 12500))
                    : options.CatchUp(drive, cursor);
            },
            watchDrive: options.WatchDrive,
            queryVolumeInfo: _ => options.VolumeInformation ?? new NtfsVolumeInformation(1024 * 100, 1024, 0, 0, 0, 0),
            scanDrive: options.RecordBatches ?? ((_, _, _) => options.SourceFails
                ? throw new IOException("batch failed")
                : [[new MftRecord(5, 5, new MftRecordFields(3), ".", null),
                    new MftRecord(20, 5, new MftRecordFields(1), "file.txt", null)]]));
        _serving = host.ServeAsync(server, _writer, false, _timeout.Token);
    }

    public InProcessBlockBrokerHarness(Action<BlockFile>? changeHeader = null, bool sourceFails = false,
        MftRecordBatchSource? recordBatches = null, UsnJournalCatchUpSource? catchUp = null,
        NtfsVolumeInformation? volumeInformation = null, UsnJournalCursorQuery? queryCursor = null)
        : this(new Options
        {
            ChangeHeader = changeHeader,
            SourceFails = sourceFails,
            RecordBatches = recordBatches,
            CatchUp = catchUp,
            VolumeInformation = volumeInformation,
            QueryCursor = queryCursor
        })
    {
    }

    public InProcessBlockBrokerHarness(
        MftRecordBatchSource? recordBatches,
        UsnJournalCatchUpSource? catchUp,
        JournalBatchSource? watchDrive)
        : this(new Options
        {
            RecordBatches = recordBatches,
            CatchUp = catchUp,
            WatchDrive = watchDrive
        })
    {
    }

    public JournalBrokerClient Client { get; }
    public bool ClientDisposed { get; set; }
    public CountingLifetime Lifetime { get; private set; } = new();
    public BlockFile? CreatedBlock { get; private set; }
    public string SectionName { get; private set; } = string.Empty;
    public CancellationToken CancellationToken => _timeout.Token;
    public MftBlockProduceRequest Request { get; } = new()
    {
        DriveLetter = 'c',
        VolumeSerial = 123,
        DeleteOnClose = true,
        BlockPath = Path.Combine(Path.GetTempPath(), $"producer-{Guid.NewGuid():N}.bin")
    };

    public Task<JournalBrokerClient> ConnectAsync(CancellationToken cancellationToken) => Task.FromResult(Client);

    public async ValueTask DisposeAsync()
    {
        await _timeout.CancelAsync();
        await _serving;
        if (!ClientDisposed)
        {
            await Client.DisposeAsync();
        }

        await _server.DisposeAsync();
        _writer.Dispose();
        foreach (var block in _sections.Values)
        {
            block.Dispose();
        }

        _timeout.Dispose();
    }

    internal sealed class CountingLifetime : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

}
