using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A <see cref="BrokerProcess" /> connected to an in-process <see cref="JournalBrokerHost" />
///     through <see cref="BrokerTestHarness" />, with the client's block sections recorded so a
///     test can see which were released. The host's section writer writes into the section the
///     client created for the scan.
/// </summary>
internal sealed class InProcessBroker : IAsyncDisposable
{
    public InProcessBroker(JournalBrokerHost host, BrokerTestHarnessOptions? options = null,
        Func<string, Stream, Stream>? wrapClientStream = null,
        Func<BrokerChannelConnector, BrokerChannelConnector>? wrapConnector = null)
    {
        Writer = new RecordingBlockSectionWriter(Sections.Resolve);
        Process = BrokerTestHarness.Start(host, Writer, Sections.Create, options ?? new BrokerTestHarnessOptions(),
            wrapClientStream, wrapConnector);
    }

    public BrokerProcess Process { get; }

    public TestBlockSections Sections { get; } = new();

    public RecordingBlockSectionWriter Writer { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Process.DisposeAsync().AsTask().WaitAsync(HostChannelHarness.HangGuard);
        }
        finally
        {
            Writer.Dispose();
            Sections.Dispose();
        }
    }
}

/// <summary>Client block sections over temporary delete-on-close files, each with a lifetime that records its disposal.</summary>
internal sealed class TestBlockSections : IDisposable
{
    readonly ConcurrentDictionary<string, (BlockFile Block, RecordingLifetime Lifetime)> _sections =
        new(StringComparer.Ordinal);

    int _next;

    public (string SectionName, BlockFile Block, IDisposable Lifetime) Create(char driveLetter,
        BlockFileCreateOptions options)
    {
        var sectionName = $"section-{driveLetter}-{Interlocked.Increment(ref _next)}";
        var block = BlockFile.Create(options);
        var lifetime = new RecordingLifetime();
        _sections[sectionName] = (block, lifetime);
        return (sectionName, block, lifetime);
    }

    public BlockFile? Resolve(string sectionName) =>
        _sections.TryGetValue(sectionName, out var section) ? section.Block : null;

    /// <summary>The one section created so far.</summary>
    public (string SectionName, BlockFile Block, RecordingLifetime Lifetime) Single()
    {
        var (sectionName, section) = _sections.Single();
        return (sectionName, section.Block, section.Lifetime);
    }

    public IReadOnlyList<(BlockFile Block, RecordingLifetime Lifetime)> All() => _sections.Values.ToList();

    /// <summary>The one section created so far for <paramref name="driveLetter" />.</summary>
    public (string SectionName, BlockFile Block, RecordingLifetime Lifetime) ForDrive(char driveLetter)
    {
        var (sectionName, section) = _sections.Single(pair => pair.Key.StartsWith($"section-{driveLetter}-", StringComparison.Ordinal));
        return (sectionName, section.Block, section.Lifetime);
    }

    public void Dispose()
    {
        foreach (var (block, _) in _sections.Values)
        {
            block.Dispose();
        }
    }

    /// <summary>A path for a new block under the temporary directory.</summary>
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
