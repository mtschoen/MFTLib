using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    [TestMethod]
    [DataRow("cursor rendering failed")]
    [DataRow("temporary output deleted")]
    [DataRow("wrapped transport failed")]
    [DataRow("rescan UI failed")]
    [DataRow("worker not active")]
    [DataRow("deletion notification failed")]
    [DataRow("view recreated")]
    [DataRow("request 1181 failed")]
    [DataRow("request 1179 failed")]
    [DataRow("request 1178 failed")]
    public async Task StartWatch_RetainedCursor_PreservesMessageDespiteFormerKeywords(string message)
    {
        var queriedDrives = new List<char>();
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(drive =>
        {
            queriedDrives.Add(drive);
            return new JournalWindow(7UL, 100L, 1000L, 64L, 4096L);
        });

        Assert.AreEqual(message, await ReadStartupErrorAsync(message));
        CollectionAssert.AreEqual(new[] { 'C' }, queriedDrives);
    }

    [TestMethod]
    [DataRow(7L, 101L)]
    [DataRow(8L, 0L)]
    public async Task StartWatch_LostCursor_DecoratesWithoutMessageKeywords(long journalId, long firstUsn)
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ =>
            new JournalWindow((ulong)journalId, firstUsn, 1000L, 64L, 4096L));

        const string message = "Read failed: opaque native diagnostic";
        Assert.AreEqual(
            "Drive C cannot resume its live watch from journal cursor 7:100: " +
            message + ". The records between that cursor and the current journal position " +
            "are gone, so this drive needs a rescan before it can be watched again.",
            await ReadStartupErrorAsync(message));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartWatch_UnknownJournal_PreservesOriginalMessage(bool incoherent)
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => incoherent
            ? new JournalWindow(8UL, 2000L, 1000L, 64L, 4096L)
            : null);

        const string message = "cursor deleted; 1181";
        Assert.AreEqual(message, await ReadStartupErrorAsync(message));
    }

    [TestMethod]
    [DataRow("C:0:0:1")]
    [DataRow(":7:100:1")]
    [DataRow("invalid:7:100:1")]
    public async Task StartWatch_NoQueryableCachedCursor_DoesNotReadJournal(string specification)
    {
        var queryCount = 0;
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ =>
        {
            queryCount++;
            return new JournalWindow(8UL, 200L, 1000L, 64L, 4096L);
        });

        const string message = "cursor operation failed";
        Assert.AreEqual(message, await ReadStartupErrorAsync(message, specification));
        Assert.AreEqual(0, queryCount);
    }

    async Task<string> ReadStartupErrorAsync(string message, string specification = "C:7:100:1")
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var client = clientSide;
        await using var server = serverSide;
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var host = CreateHost(
            _ => new UsnJournalCursor(7UL, 1000L),
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, _) => throw new InvalidOperationException(message));

        await WriteStartWatchAsync(client, specification, cancellationSource.Token);
        var serveTask = host.ServeAsync(server, CreateSectionWriter(), false, cancellationSource.Token);
        try
        {
            var frame = await ReadOneFrameAsync(client).WaitAsync(cancellationSource.Token);
            if (frame.Kind == BrokerFrameKind.CaughtUp)
            {
                frame = await ReadOneFrameAsync(client).WaitAsync(cancellationSource.Token);
            }

            Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
            Assert.AreEqual(specification.Split(':')[0], frame.Drive);
            Assert.AreEqual(1U, frame.ArmEpoch);
            Assert.AreEqual(BrokerFrameKind.EndWatchAck,
                (await EndWatchAndReadAcknowledgementAsync(client, cancellationSource.Token)).Kind);
            await ShutdownAsync(client, cancellationSource.Token);
            await serveTask.WaitAsync(cancellationSource.Token);
            return frame.RequireMessage();
        }
        finally
        {
            await cancellationSource.CancelAsync();
            await serveTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
