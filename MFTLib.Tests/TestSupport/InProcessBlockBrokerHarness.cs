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

    public InProcessBlockBrokerHarness(Action<BlockFile>? changeHeader = null, bool sourceFails = false,
        MftRecordBatchSource? recordBatches = null, UsnJournalCatchUpSource? catchUp = null,
        NtfsVolumeInformation? volumeInformation = null, UsnJournalCursorQuery? queryCursor = null)
    {
        var (client, server) = DuplexStream.CreatePair();
        _server = server;
        _writer = new RecordingBlockSectionWriter(name => _sections[name]);
        Client = new JournalBrokerClient(client,
            (_, options) =>
            {
                CreatedBlock = BlockFile.Create(options);
                SectionName = $"test-section-{Guid.NewGuid():N}";
                Lifetime = new CountingLifetime();
                _sections.Add(SectionName, CreatedBlock);
                return (SectionName, CreatedBlock, Lifetime);
            });
        var host = new JournalBrokerHost(queryCursor ?? (_ => ArmedCursor),
            readJournal: (drive, cursor) =>
            {
                changeHeader?.Invoke(CreatedBlock!);
                return catchUp == null
                    ? ([JournalEntryFactory.Create(20, 12400, "file.txt")], new UsnJournalCursor(71, 12500))
                    : catchUp(drive, cursor);
            },
            queryVolumeInfo: _ => volumeInformation ?? new NtfsVolumeInformation(1024 * 100, 1024, 0, 0, 0, 0),
            scanDrive: recordBatches ?? ((_, _, _) => sourceFails
                ? throw new IOException("batch failed")
                : [[new MftRecord(5, 5, new MftRecordFields(3), ".", null),
                    new MftRecord(20, 5, new MftRecordFields(1), "file.txt", null)]]));
        _serving = host.ServeAsync(server, _writer, false, _timeout.Token);
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
