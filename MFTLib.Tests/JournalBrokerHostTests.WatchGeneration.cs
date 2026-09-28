using System.Runtime.CompilerServices;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    /// <summary>
    ///     Reachability of a stray EndWatch: a generation whose every drive reader ended on its own,
    ///     one cleanly and one by faulting, is still the live generation, so the routine EndWatch
    ///     that follows is acknowledged with that generation's number rather than meeting a host
    ///     with nothing to end.
    /// </summary>
    [TestMethod]
    public async Task EndWatch_AfterEveryDriveReaderEndedOnItsOwn_AcknowledgesTheStillLiveGeneration()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var cleanReaderEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (drive, _, cancellationToken) =>
                drive == "C" ? EndAtOnceAsync(cleanReaderEnded) : FaultAtOnceAsync(cancellationToken));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cancellationSource.Token;
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, token);
        await WriteStartWatchAsync(clientSide, "C:7:100:1,D:8:200:2", token, watchGeneration: 7U);
        Assert.AreEqual(BrokerFrameKind.Error, (await ReadOneFrameAsync(clientSide).WaitAsync(token)).Kind);
        await cleanReaderEnded.Task.WaitAsync(token);

        await WriteBrokerRequestAsync(clientSide, BrokerProtocol.WriteEndWatch, token);
        var acknowledgement = await ReadOneFrameAsync(clientSide).WaitAsync(token);
        Assert.AreEqual(BrokerFrameKind.EndWatchAck, acknowledgement.Kind);
        Assert.AreEqual(7U, acknowledgement.WatchGeneration);

        await ShutdownAsync(clientSide, token);
        await serveTask;
    }

    /// <summary>
    ///     A stray EndWatch, one that finds no live generation, neither throws nor ends the session.
    ///     Before any watch it is answered with nothing; after one it is answered with the most
    ///     recently ended generation, which only a stop of that generation accepts. Either way the
    ///     next StartWatch on the connection arms and delivers.
    /// </summary>
    [TestMethod]
    public async Task EndWatch_WithNoLiveGeneration_KeepsServingAndTheNextStartWatchDelivers()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (drive, since, cancellationToken) =>
            {
                var invocation = new WatchInvocation(drive, since, cancellationToken);
                invocation.QueueBatch(new UsnJournalCursor(since.JournalId, since.NextUsn + 10));
                return invocation.ReadBatchesAsync(cancellationToken);
            });

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cancellationSource.Token;
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, token);

        // Before any watch: no reply, so the first frame read is the first watch's batch.
        await WriteBrokerRequestAsync(clientSide, BrokerProtocol.WriteEndWatch, token);
        await WriteStartWatchAsync(clientSide, "C:7:100:1", token, watchGeneration: 1U);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, (await ReadOneFrameAsync(clientSide).WaitAsync(token)).Kind);
        await WriteBrokerRequestAsync(clientSide, BrokerProtocol.WriteEndWatch, token);
        Assert.AreEqual(1U, (await ReadOneFrameAsync(clientSide).WaitAsync(token)).WatchGeneration);

        // After a watch ended: the stray EndWatch is answered with the ended generation again.
        await WriteBrokerRequestAsync(clientSide, BrokerProtocol.WriteEndWatch, token);
        var strayAcknowledgement = await ReadOneFrameAsync(clientSide).WaitAsync(token);
        Assert.AreEqual(BrokerFrameKind.EndWatchAck, strayAcknowledgement.Kind);
        Assert.AreEqual(1U, strayAcknowledgement.WatchGeneration);

        await WriteStartWatchAsync(clientSide, "C:7:110:2", token, watchGeneration: 2U);
        var batch = await ReadOneFrameAsync(clientSide).WaitAsync(token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);
        Assert.AreEqual(2U, batch.ArmEpoch);

        await ShutdownAsync(clientSide, token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_WithAGenerationConflictingWithTheLiveOne_EndsTheSession()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (drive, since, cancellationToken) =>
                new WatchInvocation(drive, since, cancellationToken).ReadBatchesAsync(cancellationToken));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cancellationSource.Token;
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, token);
        await WriteStartWatchAsync(clientSide, "C:7:100:1", token, watchGeneration: 1U);
        await WriteStartWatchAsync(clientSide, "D:8:200:2", token, watchGeneration: 2U);

        var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => serveTask.WaitAsync(token));
        StringAssert.Contains(failure.Message, "Conflicting watch generation 2");
    }

    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> EndAtOnceAsync(
        TaskCompletionSource ended)
    {
        await Task.Yield();
        ended.TrySetResult();
        yield break;
    }

    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> FaultAtOnceAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The drive's journal read failed.");
        }

        yield break;
    }
}
