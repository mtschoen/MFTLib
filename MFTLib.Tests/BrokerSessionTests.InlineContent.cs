using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerSessionTests
{
    // The session's own source, through an in-process broker: its reader asks the broker for the scanned record, on the
    // volume and with the serial of the block the session's scan produced, and returns the broker's bytes.
    [TestMethod]
    public async Task CreateIndexSource_ReadInlineContent_ReturnsTheBrokersBytes()
    {
        using var roots = new TempDirectory("mftlib-session-inline-roots");
        byte[] content = [0x73, 0x6D, 0x61, 0x6C, 0x6C];
        var hostReads = new List<(VolumeKey Volume, ulong Serial, long[] Records)>();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ScanVolume = (_, _, _, _, _, _) =>
                [[BrokerHostFactory.Record(5, ".", 3), BrokerHostFactory.Record(40, "small.txt")]],
            ReadInlineContent = (readVolume, readSerial, requests, sink, _) =>
            {
                hostReads.Add((readVolume, readSerial, requests.ToArray().Select(request => request.RecordNumber).ToArray()));
                foreach (var request in requests.Span)
                {
                    sink(request.Position, InlineContentOutcome.Returned, content);
                }
            }
        }));
        var brokerProcess = broker.Process;
        await using var session = CreateSession(_ => Task.FromResult(brokerProcess));
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            CacheMode = CacheMode.Disabled,
            MftSource = session.CreateIndexSource()
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('X'), roots.Path, 123)), CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);
        var entry = index.Search(new SearchQuery("small.txt"), TestContext.CancellationToken).Single();
        var answers = new List<(InlineContentOutcome Outcome, byte[] Content)>();

        var result = await index.ReadInlineContentAsync([entry],
                answer => answers.Add((answer.Outcome, answer.Content.ToArray())), TestContext.CancellationToken)
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome);
        Assert.IsNull(result.Failure);
        var (volume, serial, records) = hostReads.Single();
        Assert.AreEqual(new VolumeKey('X'), volume);
        Assert.AreEqual(123UL, serial);
        Assert.AreSequenceEqual([40L], records);
        var (outcome, bytes) = answers.Single();
        Assert.AreEqual(InlineContentOutcome.Returned, outcome);
        Assert.AreSequenceEqual(content, bytes);
    }
}
