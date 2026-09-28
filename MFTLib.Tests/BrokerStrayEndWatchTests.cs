using System.Buffers;
using System.Threading.Channels;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     A client stop whose EndWatch reaches a broker with no live generation. The client itself
///     never sends a second EndWatch for one watch, so the test puts a stray EndWatch on the pipe
///     behind its back: the broker ends the watch and acknowledges it, the client's demux accepts
///     that acknowledgement as its own, and the client's routine stop then sends an EndWatch the
///     broker has nothing left to end.
/// </summary>
[TestClass]
public sealed class BrokerStrayEndWatchTests
{
    [TestMethod]
    public async Task ClientStop_WhoseEndWatchFindsNoLiveGeneration_CompletesAndTheNextWatchDelivers()
    {
        var batches = Channel.CreateUnbounded<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();
        Stream? clientTransport = null;
        await using var harness = new InProcessBlockBrokerHarness(new InProcessBlockBrokerHarness.Options
        {
            WatchDrive = (_, _, token) => batches.Reader.ReadAllAsync(token),
            WrapClientTransport = transport => clientTransport = transport
        });
        var client = harness.Client;
        var token = harness.CancellationToken;
        var cursors = new Dictionary<string, UsnJournalCursor> { ["C"] = InProcessBlockBrokerHarness.ArmedCursor };

        await client.SendStartWatchAsync(cursors, token);
        var firstWatch = client.CreateBatchSource()("C", InProcessBlockBrokerHarness.ArmedCursor, token)
            .GetAsyncEnumerator(token);
        var strayEndWatch = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatch(strayEndWatch);
        await clientTransport!.WriteAsync(strayEndWatch.WrittenMemory, token);
        await clientTransport.FlushAsync(token);
        Assert.IsFalse(await firstWatch.MoveNextAsync(), "The broker's acknowledgement did not end the first watch.");
        await firstWatch.DisposeAsync();

        await client.StopLiveWatchAsync(token);

        await client.SendStartWatchAsync(cursors, token);
        var secondWatch = client.CreateBatchSource()("C", InProcessBlockBrokerHarness.ArmedCursor, token)
            .GetAsyncEnumerator(token);
        batches.Writer.TryWrite(([JournalEntryFactory.Create(30, 12400, "after-stray.txt")],
            new UsnJournalCursor(71, 12401)));
        Assert.IsTrue(await secondWatch.MoveNextAsync(), "The second watch ended instead of delivering.");
        Assert.AreEqual("after-stray.txt", secondWatch.Current.Entries[0].FileName);
        await secondWatch.DisposeAsync();

        await client.StopLiveWatchAsync(token);
    }
}
