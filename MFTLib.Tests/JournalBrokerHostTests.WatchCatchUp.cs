using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task StartWatch_WithNoBacklog_WritesCaughtUpBeforeTheFirstBatch()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => new UsnJournalCursor(7UL, 100L),
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, cancellationToken) => FakeWatch(
                [([SampleEntry()], new UsnJournalCursor(7UL, 110L))], cancellationToken));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:7:100:1", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var first = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, first.Kind);
        Assert.AreEqual("C", first.Drive);
        Assert.AreEqual(1U, first.ArmEpoch);
        var second = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, second.Kind);

        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);
        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_WithBacklog_WritesCaughtUpAfterTheBatchThatPassesTheTip()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => new UsnJournalCursor(7UL, 150L),
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, cancellationToken) => FakeWatch(
                [
                    ([SampleEntry()], new UsnJournalCursor(7UL, 140L)),
                    ([SampleEntry()], new UsnJournalCursor(7UL, 160L))
                ], cancellationToken));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:7:100:1", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var first = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, first.Kind);
        Assert.AreEqual(new UsnJournalCursor(7UL, 140L), first.Cursor);
        var second = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, second.Kind);
        Assert.AreEqual(new UsnJournalCursor(7UL, 160L), second.Cursor);
        var third = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, third.Kind);
        Assert.AreEqual(1U, third.ArmEpoch);

        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);
        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_WhileBelowTheTip_WritesNoCaughtUp()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => new UsnJournalCursor(7UL, 150L),
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, cancellationToken) => FakeWatch(
                [([SampleEntry()], new UsnJournalCursor(7UL, 140L))], cancellationToken));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:7:100:1", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var first = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, first.Kind);
        // Deterministic ordering: the next frame the host writes is the EndWatchAck, which proves
        // no CaughtUp sat between it and the batch.
        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);
        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_ZeroCursorSentinel_WritesCaughtUpImmediately()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => new UsnJournalCursor(8UL, 50L),
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            // An empty watch keeps the frame sequence deterministic: nothing but the marker
            // precedes the ack, because there is no batch that could race the EndWatch.
            (_, _, cancellationToken) => FakeWatch([], cancellationToken));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:0:0:1", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var first = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, first.Kind);
        Assert.AreEqual("C", first.Drive);
        Assert.AreEqual(1U, first.ArmEpoch);

        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);
        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_QueriesTheJournalTipOncePerArm()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var queryCallCount = 0;
        var host = CreateHost(
            _ =>
            {
                queryCallCount++;
                return new UsnJournalCursor(7UL, 150L);
            },
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, cancellationToken) => FakeWatch(
                [([SampleEntry()], new UsnJournalCursor(7UL, 140L))], cancellationToken));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:7:100:1", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var first = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, first.Kind);
        Assert.AreEqual(1, queryCallCount);

        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);
        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_TipQueryFailure_EndsTheDrivesStreamWithAnError()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => throw new UnauthorizedAccessException("Access is denied"),
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, cancellationToken) => FakeWatch(
                [([SampleEntry()], new UsnJournalCursor(7UL, 110L))], cancellationToken));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteStartWatchAsync(clientSide, "C:7:100:4", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var error = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        Assert.AreEqual("C", error.Drive);
        Assert.AreEqual(4U, error.ArmEpoch);
        Assert.AreEqual("Access is denied", error.Message);

        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);
        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }
}
