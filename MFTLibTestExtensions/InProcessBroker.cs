using System.Collections.Concurrent;
using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     A <see cref="BrokerProcess" /> connected to an in-process <see cref="JournalBrokerHost" />
///     through <see cref="BrokerTestHarness" />, with the client's block sections recorded so a
///     test can see which were released. The host's section writer writes into the section the
///     client created for the scan.
/// </summary>
internal sealed class InProcessBroker : IAsyncDisposable
{
    readonly ScriptedBrokerResources _resources;

    public InProcessBroker(JournalBrokerHost host, BrokerTestHarnessOptions? options = null,
        Func<string, Stream, Stream>? wrapClientStream = null,
        Func<BrokerChannelConnector, BrokerChannelConnector>? wrapConnector = null)
    {
        _resources = ScriptedBrokerResources.Create();
        Handle = BrokerTestHarness.Start(host, _resources,
            options ?? new BrokerTestHarnessOptions(), wrapClientStream, wrapConnector);
    }

    public InProcessBrokerHandle Handle { get; }

    public BrokerProcess Process => Handle.Process;

    public TestBlockSections Sections => _resources.Sections;

    public RecordingBlockSectionWriter Writer => _resources.Writer;

    public async ValueTask DisposeAsync() =>
        await Handle.DisposeAsync().AsTask().WaitAsync(TestTimeouts.HangGuard);
}

/// <summary>Client block sections over temporary delete-on-close files, each with a lifetime that records its disposal.</summary>
internal sealed class TestBlockSections : IDisposable
{
    readonly ConcurrentDictionary<string, (BlockFile Block, RecordingLifetime Lifetime)> _sections =
        new(StringComparer.Ordinal);

    int _next;

    public (string SectionName, BlockFile Block, IDisposable Lifetime) Create(DriveKey drive,
        BlockFileCreateOptions options)
    {
        var sectionName = $"section-{drive}-{Interlocked.Increment(ref _next)}";
        var block = BlockFile.Create(options);
        var lifetime = new RecordingLifetime();
        _sections[sectionName] = (block, lifetime);
        return (sectionName, block, lifetime);
    }

    public BlockFile? Resolve(string sectionName) =>
        _sections.TryGetValue(sectionName, out var section) ? section.Block : null;

    /// <summary>Throws unless exactly one section was created, so a test that expects one scan fails when it sees two.</summary>
    public (string SectionName, BlockFile Block, RecordingLifetime Lifetime) Single()
    {
        var (sectionName, section) = _sections.Single();
        return (sectionName, section.Block, section.Lifetime);
    }

    public IReadOnlyList<(BlockFile Block, RecordingLifetime Lifetime)> All() => _sections.Values.ToList();

    /// <summary>The one section created so far for <paramref name="drive" />.</summary>
    public (string SectionName, BlockFile Block, RecordingLifetime Lifetime) ForDrive(DriveKey drive)
    {
        var (sectionName, section) = _sections.Single(pair => pair.Key.StartsWith($"section-{drive}-", StringComparison.Ordinal));
        return (sectionName, section.Block, section.Lifetime);
    }

    public void Dispose()
    {
        foreach (var (block, _) in _sections.Values)
        {
            block.Dispose();
        }
    }

    /// <summary>Every call names a new file, so scans that overlap never share a block.</summary>
    public static BlockScanTarget Target(uint volumeSerial = 123) =>
        new(Path.Combine(Path.GetTempPath(), $"broker-process-block-{Guid.NewGuid():N}.bin"), volumeSerial,
            DeleteOnClose: true);
}

internal sealed class RecordingLifetime : IDisposable
{
    readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _disposeCount;

    /// <summary>How many times the lifetime was disposed; the client releases each section exactly once.</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public bool IsDisposed => DisposeCount > 0;

    /// <summary>Completes the first time the lifetime is disposed.</summary>
    public Task Disposed => _disposed.Task;

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        _disposed.TrySetResult();
    }
}
