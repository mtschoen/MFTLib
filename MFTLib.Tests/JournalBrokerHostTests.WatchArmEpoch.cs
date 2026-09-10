using System.Buffers;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    public void ParseWatchSpec_ReadsTheArmEpochAsTheFourthField()
    {
        var requests = JournalBrokerHost.ParseWatchSpecForTest("C:7:100:1,D:2:200:9");

        Assert.AreEqual(2, requests.Length);
        Assert.AreEqual(new JournalBrokerHost.WatchDriveRequest("C", 7UL, 100L, 1U), requests[0]);
        Assert.AreEqual(new JournalBrokerHost.WatchDriveRequest("D", 2UL, 200L, 9U), requests[1]);
    }

    [TestMethod]
    public void ParseWatchSpec_RejectsATokenWithNoArmEpoch()
    {
        var exception = Assert.ThrowsException<InvalidDataException>(
            () => JournalBrokerHost.ParseWatchSpecForTest("C:7:100"));

        StringAssert.Contains(exception.Message, "C:7:100");
    }

    [TestMethod]
    public void ParseWatchSpec_NormalizesTheDriveLetterItArmsUnder()
    {
        var request = JournalBrokerHost.ParseWatchSpecForTest("c:7:100:1").Single();

        Assert.AreEqual("C", request.Letter);
    }

    [TestMethod]
    public async Task StartWatch_TagsEveryJournalBatchForADriveWithThatArmsEpoch()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, cancellationToken) => FakeWatch(
            [
                ([SampleEntry()], new UsnJournalCursor(7UL, 110L)),
                ([SampleEntry()], new UsnJournalCursor(7UL, 120L))
            ], cancellationToken));
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await WriteStartWatchAsync(clientSide, "C:7:100:4", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var first = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        var second = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, first.Kind);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, second.Kind);
        Assert.AreEqual(4U, first.ArmEpoch);
        Assert.AreEqual(4U, second.ArmEpoch);

        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);
        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_ReArmingADriveTagsItsBatchesWithTheNewEpochOnly()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var invocations = new List<WatchInvocation>();
        var host = CreateRecordingWatchHost(invocations);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await WriteStartWatchAsync(clientSide, "C:7:100:4", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);
        var firstInvocation = (await WaitForInvocationCountAsync(invocations, 1, cancellationSource.Token)).Single();
        firstInvocation.QueueBatch(new UsnJournalCursor(7UL, 110L));
        var firstBatch = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(4U, firstBatch.ArmEpoch);

        await WriteStartWatchAsync(clientSide, "C:7:500:5", cancellationSource.Token);
        var secondInvocation = (await WaitForInvocationCountAsync(invocations, 2, cancellationSource.Token))[1];
        await firstInvocation.Stopped.Task.WaitAsync(cancellationSource.Token);
        secondInvocation.QueueBatch(new UsnJournalCursor(7UL, 510L));
        secondInvocation.QueueBatch(new UsnJournalCursor(7UL, 520L));

        var frames = new[]
        {
            await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token),
            await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token)
        };
        Assert.IsTrue(frames.All(frame => frame.Kind == BrokerFrameKind.JournalBatch));
        Assert.IsTrue(frames.All(frame => frame.ArmEpoch == 5U));

        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);
        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_TagsAPerDriveErrorWithTheArmEpochThatProducedIt()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => throw new AssertFailedException("A cached cursor must not be re-queried."),
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, _) => throw new InvalidOperationException(
                "USN journal entries have been deleted; full rescan needed"));
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await WriteStartWatchAsync(clientSide, "C:7:100:4", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var error = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        Assert.AreEqual(4U, error.ArmEpoch);
        StringAssert.Contains(error.RequireMessage(), "7:100");
        StringAssert.Contains(error.RequireMessage(), "USN journal entries have been deleted; full rescan needed");
        StringAssert.Contains(error.RequireMessage(), "rescan");

        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);
        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task StartWatch_WithNoWatchSource_TagsItsErrorWithTheArmEpoch()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await WriteStartWatchAsync(clientSide, "C:0:0:2", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);

        var error = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        Assert.AreEqual(2U, error.ArmEpoch);
        Assert.AreEqual("Broker has no watch source", error.Message);

        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);
        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task DisarmDrive_ForADriveSpelledAsAPath_RetiresTheDriveThatWasArmed()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var invocations = new List<WatchInvocation>();
        var host = CreateRecordingWatchHost(invocations);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await WriteStartWatchAsync(clientSide, "C:7:100:1,D:2:200:2", cancellationSource.Token);
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cancellationSource.Token);
        var started = await WaitForInvocationCountAsync(invocations, 2, cancellationSource.Token);
        var driveC = started.Single(invocation => invocation.Drive == "C");
        var driveD = started.Single(invocation => invocation.Drive == "D");

        await WriteDisarmDriveAsync(clientSide, "C:\\", cancellationSource.Token);
        await driveC.Stopped.Task.WaitAsync(cancellationSource.Token);
        Assert.IsFalse(driveD.CancellationToken.IsCancellationRequested);
        driveD.QueueBatch(new UsnJournalCursor(2UL, 210L));
        var batch = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);
        Assert.AreEqual("D", batch.Drive);
        Assert.AreEqual(2U, batch.ArmEpoch);

        Assert.AreEqual(BrokerFrameKind.EndWatchAck,
            (await EndWatchAndReadAcknowledgementAsync(clientSide, cancellationSource.Token)).Kind);
        await ShutdownAsync(clientSide, cancellationSource.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task ArmAndScan_WritesItsPerDriveErrorWithNoArmEpoch()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = CreateHost(
            _ => throw new InvalidOperationException("scan failed"),
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-scan-C");
        await clientSide.WriteAsync(request.WrittenMemory, cancellationSource.Token);
        await clientSide.FlushAsync(cancellationSource.Token);

        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), true, cancellationSource.Token);
        var error = await ReadOneFrameAsync(clientSide).WaitAsync(cancellationSource.Token);

        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        Assert.AreEqual(BrokerFrame.NoArmEpoch, error.ArmEpoch);
        await serveTask;
    }
}
