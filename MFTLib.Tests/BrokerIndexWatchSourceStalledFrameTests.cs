using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     A stop whose acknowledgement wait is cancelled while the broker is stalled inside a frame
///     leaves the client's reader retired inside that frame. The next start on the watch source
///     waits for that reader before sending anything, and that wait must be bounded by the start's
///     own token: when it runs out, the connection is failed rather than read from mid-frame, and
///     the source is released rather than left claimed by a send that can never finish.
/// </summary>
[TestClass]
public sealed class BrokerIndexWatchSourceStalledFrameTests
{
    static readonly IndexWatchTarget TargetC = new('C', 7, 100);

    [TestMethod]
    public async Task StartCancelledWhileAStoppedWatchsFrameIsStalled_FailsTheConnectionAndReleasesTheSource()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var peer = serverSide;
        await using var observed = new ReadObservingStream(clientSide);
        await using var client = new JournalBrokerClient(observed,
            (_, _) => throw new InvalidOperationException("Block creation is not expected."));
        string? deathReason = null;
        client.BrokerDied += reason => deathReason = reason;
        using var hangGuard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = hangGuard.Token;
        var connection = Task.FromResult(client);
        var source = new BrokerIndexWatchSource(_ => connection);

        // A running stream whose broker sends only the length prefix of a frame, then nothing.
        using var firstStreamCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var stopCancellation = new CancellationTokenSource();
        var first = source.StartWatching([TargetC], static () => { }, stopCancellation.Token,
                firstStreamCancellation.Token)
            .GetAsyncEnumerator(firstStreamCancellation.Token);
        var firstMove = first.MoveNextAsync().AsTask();
        var firstStart = await ReadFrameAsync(serverSide, token);
        Assert.AreEqual(BrokerFrameKind.StartWatch, firstStart.Kind);
        var caughtUp = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteCaughtUp(caughtUp, "C", WatchSpecArmEpochs.ForDrive(firstStart, "C"));
        await serverSide.WriteAsync(caughtUp.WrittenMemory[..4], token);
        await serverSide.FlushAsync(token);
        // The demux's first read took the header; its second read is the one waiting for the body.
        await observed.ReadsIssued(2).WaitAsync(token);

        // The stop FileIndex.StopWatchingAsync makes, with its acknowledgement wait cut short.
        await firstStreamCancellation.CancelAsync();
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await ReadFrameAsync(serverSide, token)).Kind);
        await stopCancellation.CancelAsync();
        await AssertCancelledAsync(firstMove, token);
        await first.DisposeAsync();

        // The next start reaches the wait for the retired reader before its first move returns.
        using var startCancellation = new CancellationTokenSource();
        var second = source.StartWatching([TargetC], startCancellation.Token)
            .GetAsyncEnumerator(startCancellation.Token);
        var secondMove = second.MoveNextAsync().AsTask();
        await startCancellation.CancelAsync();
        await AssertCancelledAsync(secondMove, token);
        await second.DisposeAsync();

        Assert.IsNotNull(deathReason, "The start gave up on the stalled frame without failing the connection.");
        var third = source.StartWatching([TargetC], token).GetAsyncEnumerator(token);
        var refused = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => third.MoveNextAsync().AsTask().WaitAsync(token));
        Assert.IsFalse(refused.Message.Contains("already running", StringComparison.Ordinal),
            $"The source was left claimed: {refused.Message}");
        await third.DisposeAsync();
    }

    // Either cancellation type: which one surfaces depends on where the stream observed its token.
    static async Task AssertCancelledAsync(Task<bool> move, CancellationToken hangGuard)
    {
        try
        {
            await move.WaitAsync(hangGuard);
        }
        catch (OperationCanceledException) when (!hangGuard.IsCancellationRequested)
        {
            return;
        }

        Assert.Fail("The stream ended without reporting its cancellation.");
    }

    static async Task<BrokerFrame> ReadFrameAsync(Stream server, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await server.ReadExactlyAsync(header, cancellationToken);
        var frame = new byte[4 + BinaryPrimitives.ReadInt32LittleEndian(header)];
        header.CopyTo(frame, 0);
        await server.ReadExactlyAsync(frame.AsMemory(4), cancellationToken);
        return BrokerProtocol.ReadFrame(frame, out _);
    }
}
