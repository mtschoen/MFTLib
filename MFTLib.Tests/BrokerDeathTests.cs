using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib.Tests.TestSupport;

namespace MFTLib.Tests;

/// <summary>
/// Verifies broker-death detection:
/// <list type="bullet">
///   <item>BrokerDied fires exactly once when the pipe drops.</item>
///   <item>The batch source throws InvalidOperationException (not yield-break) so
///     a journal watcher can flip the drive inactive.</item>
///   <item>A second EOF / repeated death does not re-fire BrokerDied.</item>
/// </list>
/// </summary>
[TestClass]
public class BrokerDeathTests
{
    [TestMethod]
    public async Task BatchSource_PipeDeath_FiresBrokerDiedOnce_AndThrowsInvalidOperation()
    {
        // Arrange: build a client over a DuplexStream pair.
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var deathMessages = new List<string>();
        client.BrokerDied += message => { lock (deathMessages) deathMessages.Add(message); };

        // Start the live-watch demux (single pipe reader) before subscribing per drive.
        await client.SendStartWatchAsync(WatchCursors("C"));
        var batchSource = client.CreateBatchSource();
        // Not a `using var`: the token is captured by the Task.Run lambda below, which
        // must outlive this method's own scope-exit ordering, so it is disposed
        // explicitly at the end instead - safe because that Dispose() runs only after
        // the lambda's task has already been awaited to completion below.
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act: close the broker side so the client reads EOF.
        var enumerateTask = Task.Run(async () =>
        {
            // aislop-ignore-next-line AccessToDisposedClosure
            await foreach (var _ in batchSource("C:\\", default, cts.Token)) { }
        }, cts.Token);

        // Give the enumerate task a moment to start before closing the pipe.
        await Task.Delay(20, cts.Token);
        serverSide.Dispose();

        // Assert: the enumerate task must throw InvalidOperationException (not complete normally).
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => enumerateTask);
        StringAssert.Contains(exception.Message.ToLowerInvariant(), "broker pipe closed");

        // BrokerDied must have fired exactly once.
        Assert.AreEqual(1, deathMessages.Count);

        await client.DisposeAsync();
        cts.Dispose();
    }

    [TestMethod]
    public async Task BatchSource_DeathBeforeSubscribe_LateSubscribersThrow_BrokerDiedFiresOnce()
    {
        // Arrange: start the demux while the pipe is open, then kill it. The single
        // demux reader detects death once and latches it; subscribers (even ones that
        // subscribe AFTER death) must each throw, but BrokerDied fires exactly once.
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var deathCount = 0;
        client.BrokerDied += _ => Interlocked.Increment(ref deathCount);

        await client.SendStartWatchAsync(WatchCursors("C"));
        var batchSource = client.CreateBatchSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        serverSide.Dispose(); // EOF -> demux detects death and latches it

        // A subscriber for a drive that never had a channel must still fault (the latch
        // hands it an already-completed channel) rather than awaiting a batch forever.
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in batchSource("C:\\", default, cts.Token)) { }
        });
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in batchSource("D:\\", default, cts.Token)) { }
        });

        // BrokerDied fires at most once regardless of how many subscribers see death.
        Assert.AreEqual(1, deathCount);

        await client.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    static JournalBrokerClient MakeMinimalFakeClient(Stream pipe) =>
        new(pipe,
            mmfReader: new NullMmfReader(),
            createDriveMmf: (letter, _) => ($"mftlib-null-{letter}", NoOpDisposable.Instance));

    // Cursor map for SendStartWatchAsync in the death tests (values are not asserted).
    static Dictionary<string, UsnJournalCursor> WatchCursors(params string[] drives) =>
        drives.ToDictionary(d => d, _ => new UsnJournalCursor(7UL, 0L), StringComparer.OrdinalIgnoreCase);

    sealed class NullMmfReader : IMmfReader
    {
        public ScanRecord[] Read(string mmfName, long byteLength) => Array.Empty<ScanRecord>();
    }

    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public void Dispose() { }
    }
}
