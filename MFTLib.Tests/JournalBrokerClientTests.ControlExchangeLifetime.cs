using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerClientTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ControlExchange_CancelledTransmittedQuery_RejectsLaterRequests(bool watch)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var requestCancellation = new CancellationTokenSource();
        var (transport, server) = DuplexStream.CreatePair();
        await using var peer = server;
        await using var client = MakeMinimalFakeClient(transport);
        if (watch)
        {
            await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor>
            { ["D"] = new(9, 100) }, timeout.Token);
            Assert.AreEqual(BrokerFrameKind.StartWatch,
                (await ReadControlRequestAsync(server, timeout.Token)).Kind);
        }
        var query = client.QueryVolumesAsync(DriveC, requestCancellation.Token);
        Assert.AreEqual(BrokerFrameKind.QueryVolumes,
            (await ReadControlRequestAsync(server, timeout.Token)).Kind);
        await requestCancellation.CancelAsync();
        await AssertControlCancelledAsync(query, timeout.Token);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await client.QueryVolumesAsync(DriveC, timeout.Token));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ControlExchange_DisposeDuringQuery_UnblocksTheReaderAndQueuedOperation(bool watch)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (transport, server) = DuplexStream.CreatePair();
        await using var peer = server;
        var client = MakeMinimalFakeClient(transport);
        if (watch)
        {
            await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor>
            { ["D"] = new(9, 100) }, timeout.Token);
            Assert.AreEqual(BrokerFrameKind.StartWatch,
                (await ReadControlRequestAsync(server, timeout.Token)).Kind);
        }
        var query = client.QueryVolumesAsync(DriveC);
        Assert.AreEqual(BrokerFrameKind.QueryVolumes,
            (await ReadControlRequestAsync(server, timeout.Token)).Kind);
        var queued = client.QueryVolumesAsync(DriveD);
        var disposal = client.DisposeAsync().AsTask();
        await AssertControlCancelledAsync(query, timeout.Token);
        await disposal.WaitAsync(timeout.Token);
        try { await queued.WaitAsync(timeout.Token); Assert.Fail("Queued operation succeeded after disposal."); }
        catch (OperationCanceledException ex) { Assert.IsNotNull(ex); }
        catch (ObjectDisposedException ex) { Assert.IsNotNull(ex); }
        Assert.IsFalse(timeout.IsCancellationRequested, "The hang guard, not disposal, ended the wait.");
        Assert.IsTrue(queued.IsCompleted);
    }

    static async Task AssertControlCancelledAsync(Task task, CancellationToken hangGuard)
    {
        try { await task.WaitAsync(hangGuard); Assert.Fail("Expected cancellation."); }
        catch (OperationCanceledException)
        {
            Assert.IsFalse(hangGuard.IsCancellationRequested, "The hang guard fired.");
            Assert.IsTrue(task.IsCompleted, "The underlying operation is still running.");
        }
    }

    [TestMethod]
    public async Task ControlExchange_QuerySerializesWithASecondQuery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var (transport, server) = DuplexStream.CreatePair();
        await using var peer = server;
        await using var client = MakeMinimalFakeClient(transport);
        var first = client.QueryVolumesAsync(DriveC, token);
        Assert.AreEqual("C:0:0", (await ReadControlRequestAsync(server, token)).DrivesSpec);
        var second = client.QueryVolumesAsync(DriveD, token);
        await SendControlReplyAsync(server,
            writer => BrokerProtocol.WriteVolumeInfo(writer, "C", 128, 1024, 128 * 1024), token);
        Assert.AreEqual(128L, (await first.WaitAsync(token)).Volumes["C"].MftRecordCount);
        Assert.AreEqual("D:0:0", (await ReadControlRequestAsync(server, token)).DrivesSpec);
        await SendControlReplyAsync(server,
            writer => BrokerProtocol.WriteError(writer, "D", BrokerFrame.NoArmEpoch, "volume unavailable"), token);
        Assert.AreEqual("volume unavailable", (await second.WaitAsync(token)).Errors["D"]);
    }

    [DataTestMethod]
    [DataRow("ack")]
    [DataRow("truncated")]
    [DataRow("eof")]
    public async Task ControlExchange_DemuxExit_CompletesPendingQuery(string ending)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var (transport, server) = DuplexStream.CreatePair();
        await using var peer = server;
        await using var client = MakeMinimalFakeClient(transport);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor>
        { ["D"] = new(9, 100) }, token);
        await ReadControlRequestAsync(server, token);
        var query = client.QueryVolumesAsync(DriveC, token);
        Assert.AreEqual(BrokerFrameKind.QueryVolumes, (await ReadControlRequestAsync(server, token)).Kind);
        if (ending == "ack")
        {
            await SendControlReplyAsync(server, writer => BrokerProtocol.WriteEndWatchAck(writer, 1), token);
        }
        else
        {
            if (ending == "truncated")
            {
                await server.WriteAsync(new byte[] { 10, 0, 0, 0, 1, 2, 3 }, token);
            }
            await server.DisposeAsync();
        }
        if (ending == "eof")
        {
            Assert.IsTrue((await query.WaitAsync(token)).Errors.ContainsKey("C"));
        }
        else
        {
            try { await query.WaitAsync(token); Assert.Fail("Terminal demux exit was ignored."); }
            catch (InvalidOperationException ex) when (ending == "ack") { Assert.IsNotNull(ex); }
            catch (EndOfStreamException ex) when (ending == "truncated") { Assert.IsNotNull(ex); }
        }
        Assert.IsTrue(query.IsCompleted);
        Assert.IsFalse(timeout.IsCancellationRequested);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ControlExchange_WatchHandoff_WaitsForQuery(bool stop)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var (transport, server) = DuplexStream.CreatePair();
        await using var peer = server;
        await using var client = MakeMinimalFakeClient(transport);
        var cursors = new Dictionary<string, UsnJournalCursor> { ["D"] = new(9, 100) };
        if (stop)
        {
            await client.SendStartWatchAsync(cursors, token);
            Assert.AreEqual(BrokerFrameKind.StartWatch, (await ReadControlRequestAsync(server, token)).Kind);
        }
        var query = client.QueryVolumesAsync(DriveC, token);
        Assert.AreEqual(BrokerFrameKind.QueryVolumes, (await ReadControlRequestAsync(server, token)).Kind);
        var transition = stop ? client.StopLiveWatchAsync() : client.SendStartWatchAsync(cursors, token);
        await SendControlReplyAsync(server,
            writer => BrokerProtocol.WriteVolumeInfo(writer, "C", 128, 1024, 128 * 1024), token);
        Assert.AreEqual(128L, (await query.WaitAsync(token)).Volumes["C"].MftRecordCount);
        Assert.AreEqual(stop ? BrokerFrameKind.EndWatch : BrokerFrameKind.StartWatch,
            (await ReadControlRequestAsync(server, token)).Kind);
        if (stop)
        {
            await SendControlReplyAsync(server, writer => BrokerProtocol.WriteEndWatchAck(writer, 1), token);
        }
        await transition.WaitAsync(token);
        if (stop)
        {
            await client.SendStartWatchAsync(cursors, token);
            Assert.AreEqual(BrokerFrameKind.StartWatch, (await ReadControlRequestAsync(server, token)).Kind);
        }
    }

    [TestMethod]
    public async Task ControlExchange_LiveErrorIsNotAVolumeQueryErrorForTheSameDrive()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var (transport, server) = DuplexStream.CreatePair();
        await using var peer = server;
        await using var client = MakeMinimalFakeClient(transport);
        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor>
        { ["D"] = new(9, 100) }, token);
        var start = await ReadControlRequestAsync(server, token);
        var epoch = WatchSpecArmEpochs.ForDrive(start, "D");
        await using var batches = client.CreateBatchSource()("D", default, token).GetAsyncEnumerator(token);
        var next = batches.MoveNextAsync().AsTask();
        var query = client.QueryVolumesAsync(DriveD, token);
        Assert.AreEqual(BrokerFrameKind.QueryVolumes, (await ReadControlRequestAsync(server, token)).Kind);
        await SendControlReplyAsync(server, writer =>
        {
            BrokerProtocol.WriteError(writer, "D", epoch, "live journal failed");
            BrokerProtocol.WriteVolumeInfo(writer, "D", 128, 1024, 128 * 1024);
        }, token);
        var result = await query.WaitAsync(token);
        Assert.AreEqual(128L, result.Volumes["D"].MftRecordCount);
        Assert.AreEqual(0, result.Errors.Count);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await next.WaitAsync(token));
    }
}
