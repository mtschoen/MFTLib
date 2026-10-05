using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    // A custom journal source can hand back a batch no frame can carry. The watch fails like any
    // source failure: an Error frame ends that channel, and a later watch is unaffected.
    [TestMethod]
    public async Task StartWatch_BatchOverTheFrameLimit_EndsThatWatchWithAnErrorFrame()
    {
        var oversized = Enumerable.Range(0, 30_500)
            .Select(index => JournalEntries.Create((ulong)index, index, new string('e', 255))).ToArray();
        var host = CreateWatchHost(
            watchDrive: (_, _, _, _) => FiniteWatch([(oversized, new UsnJournalCursor(7UL, 110L))]));
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.JournalBatch));
        Assert.AreEqual(BrokerFrameKind.Error, frames[^1].Kind);
        StringAssert.Contains(frames[^1].RequireMessage(), "frame limit");
    }
}
