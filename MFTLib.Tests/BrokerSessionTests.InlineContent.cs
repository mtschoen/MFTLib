using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerSessionTests
{
    // The session's own source, through an in-process broker: it carries no inline content reader yet.
    [TestMethod]
    public async Task CreateIndexSource_ReadInlineContent_IsNotApplicable()
    {
        using var roots = new TempDirectory("mftlib-session-inline-roots");
        await using var handle = BrokerTestHarness.StartInProcess(new ScriptedBrokerVolumes
        {
            QueryJournalCursor = _ => new SyntheticJournalCursor(7, 1000),
            ScanVolume = _ =>
            [
                [
                    new SyntheticScanRecord { RecordNumber = 5, ParentRecordNumber = 5, FileName = ".", IsDirectory = true },
                    new SyntheticScanRecord { RecordNumber = 40, ParentRecordNumber = 5, FileName = "small.txt" }
                ]
            ]
        });
        var brokerProcess = handle.Process;
        await using var session = CreateSession(_ => Task.FromResult(brokerProcess));
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            CacheMode = CacheMode.Disabled,
            MftSource = session.CreateIndexSource()
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('X'), roots.Path, 123)), CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);
        var entry = index.Search(new SearchQuery("small.txt"), TestContext.CancellationToken).Single();
        var calls = 0;

        var result = await index.ReadInlineContentAsync([entry], _ => calls++, TestContext.CancellationToken)
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(RootOperationOutcome.NotApplicable, result.Outcome);
        Assert.IsNull(result.Failure);
        Assert.AreEqual(0, calls);
    }
}
