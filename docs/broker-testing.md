# Testing your integration

Reference **`MFTLib.TestExtensions`** from consumer test projects. It provides an
in-process broker harness and opt-in guards against accidental access to the
real per-user cache or a real volume's USN journal.

## BrokerTestHarness

`BrokerTestHarness.StartInProcess` runs a real `JournalBrokerHost` on a
background task and returns a real `BrokerProcess` connected through in-memory
control and drive pipes. It needs no elevation and launches no child process.
Consumer tests therefore exercise the same request routing, per-drive channels,
frame decoding, timeouts, producer, and watch source used in production.

The required arguments are:

- a `JournalBrokerHost` whose cursor, scan, catch-up, watch, volume-query, and
  journal-grow delegates are test fakes;
- an `IBlockSectionWriter` that writes scan batches into the section named by
  the host request; and
- a `BrokerBlockSectionFactory` that creates the client side's block and section
  lifetime.

The returned process owns the harness session from the client side. Disposing
it closes the control pipe, ends the host, closes the drive pipes, and waits for
the session task. The harness has no separate fault event or stored host
exception. A host failure reaches the test through the production surfaces:

- `BrokerProcess.Ended` and `HasEnded`;
- `BrokerChannelLostException` on pending control or drive operations; or
- a host `Error` frame translated by the operation reading that channel.

Host exception detail is written only to broker diagnostics. Disposing the
process does not throw the host fault again.

`BrokerTestHarnessOptions` adds deterministic transport seams:

- `TimeProvider` is the client clock for write and reply timeouts;
- `FailConnection` returns the exception a named drive-pipe connection should
  throw; and
- `HoldWrites` returns a task that a named host pipe waits on before writing.
  The name is `"control"` for the control pipe or the generated drive-pipe name.

The host's clock and processor count belong to the `JournalBrokerHost`
constructor. Use those parameters to drive host deadlines and parse-thread
admission. Use `BrokerTestHarnessOptions.TimeProvider` for the client's deadlines.

For portable block-writing examples, see
`MFTLib.Tests/TestSupport/RecordingBlockSectionWriter.cs` and the broker harness
fixtures in this repository. Those helpers are repository test types, not part
of `MFTLib.TestExtensions`; consumer tests implement the same public
`IBlockSectionWriter` and `BrokerBlockSectionFactory` seams.

## Fake the FileIndex watch boundary directly

A test focused on `FileIndex` policy does not need a broker. Implement
`IIndexWatchSource.StartAsync` so each call returns one `IIndexDriveWatch` for
the requested drive. The handle yields only that drive's `JournalBatch` and
`DriveCaughtUp` items and completes by throwing a classified failure.

This minimal fake uses a channel as the drive's script:

```csharp
using System.Threading.Channels;
using MFTLib.Index;

sealed class FakeWatchSource(Action<FakeDriveWatch> started) : IIndexWatchSource
{
    public Task<IIndexDriveWatch> StartAsync(
        IndexWatchTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var watch = new FakeDriveWatch(target.DriveLetter);
        started(watch);
        return Task.FromResult<IIndexDriveWatch>(watch);
    }
}

sealed class FakeDriveWatch(char driveLetter) : IIndexDriveWatch
{
    readonly Channel<WatchStreamItem> _items =
        Channel.CreateUnbounded<WatchStreamItem>();

    public char DriveLetter { get; } = driveLetter;

    public ValueTask EmitAsync(WatchStreamItem item) =>
        _items.Writer.WriteAsync(item);

    public void Fail(Exception exception) =>
        _items.Writer.TryComplete(exception);

    public IAsyncEnumerable<WatchStreamItem> ReadAsync(
        CancellationToken cancellationToken) =>
        _items.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        _items.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
```

Return from `StartAsync` only when the handle is ready to be read. The index
starts one pump per returned handle and disposes that handle exactly once. A
test can drive the public behavior as follows:

- emit `JournalBatch` to apply changes and advance the block cursor;
- emit `DriveCaughtUp` to settle that drive's catch-up wait;
- complete with `DriveWatchFaultException` to produce
  `WatchFaultKind.Drive` and automatic recovery; or
- complete with another exception to produce `WatchFaultKind.Channel` with no
  automatic recovery.

A normal end before the index cancels the read is also a channel fault. The
handle must observe the read token promptly; this is how stop, rescan, and
dispose end its pump. Keep fakes per drive. Sharing one queue between handles
would reintroduce cross-drive ordering and failure coupling that the public seam
is designed to exclude.

## Isolate cache and journal state

Consumer test assemblies can activate both guards from a module initializer:

```csharp
using System.Runtime.CompilerServices;
using MFTLibTestExtensions;

static class TestIsolation
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        CacheDirectoryIsolation.ForbidDefaultCacheDirectory();
        JournalIsolation.ForbidLiveJournalReads();
    }
}
```

`CacheDirectoryIsolation.ForbidDefaultCacheDirectory` is process-wide,
idempotent, and one-way. After activation, resolving the default cache directory
throws `InvalidOperationException`. Every test opening an index must set
`FileIndexOptions.CacheDirectory` to a temporary directory it owns, including
`NoCache` and empty-drive tests.

`JournalIsolation.ForbidLiveJournalReads` is also process-wide, idempotent, and
one-way. It makes live journal observations answer "cannot say" instead of
reading a volume. It does not throw and does not affect broker source delegates
supplied by the test.

Use `JournalIsolation.OverrideJournalWindow` when a test needs a retained,
trimmed, or recreated journal window. The callback receives a drive letter and
returns `SyntheticJournalWindow?`; null means "cannot say" and never falls back
to a live read. The returned scope is process-global, so mark the whole fixture
nonparallel, keep the scope through all awaited work, and stop and dispose every
index and watch before disposing it. Nested or overlapping public scopes throw
`InvalidOperationException`. Mutable callback state must be synchronized because
different drive pumps can query it concurrently.
