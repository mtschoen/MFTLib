using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    static readonly UsnJournalCursor CachedCursor = new(7UL, 100L);

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

        Assert.AreEqual(message, await ReadStartupErrorAsync(message, CachedCursor));
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
            await ReadStartupErrorAsync(message, CachedCursor));
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
        Assert.AreEqual(message, await ReadStartupErrorAsync(message, CachedCursor));
    }

    [TestMethod]
    public async Task StartWatch_NoQueryableCachedCursor_DoesNotReadJournal()
    {
        var queryCount = 0;
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ =>
        {
            queryCount++;
            return new JournalWindow(8UL, 200L, 1000L, 64L, 4096L);
        });

        const string message = "cursor operation failed";
        Assert.AreEqual(message, await ReadStartupErrorAsync(message, default));
        Assert.AreEqual(0, queryCount);
    }

    static async Task<string> ReadStartupErrorAsync(string message, UsnJournalCursor since)
    {
        var host = CreateWatchHost(
            queryCursor: _ => new UsnJournalCursor(7UL, 1000L),
            watchDrive: (_, _, _, _) => throw new InvalidOperationException(message));
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', since);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        // A sentinel start reports CaughtUp before its source fails; the Error is always last.
        Assert.AreEqual(BrokerFrameKind.Error, frames[^1].Kind);
        Assert.IsTrue(frames.SkipLast(1).All(frame => frame.Kind == BrokerFrameKind.CaughtUp));
        return frames[^1].RequireMessage();
    }
}
