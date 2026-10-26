using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public class DriveKeyLifecycleTests
{
    [TestMethod]
    [DataRow("StartWatchingAsync")]
    [DataRow("StopWatchingAsync")]
    [DataRow("WaitForCatchUpAsync")]
    [DataRow("RescanAsync")]
    public void LifecycleVerb_HasOneEnumerableMethod(string name)
    {
        var methods = typeof(FileIndex).GetMethods().Where(method => method.Name == name).ToArray();
        Assert.HasCount(1, methods);
        Assert.AreSequenceEqual([typeof(IEnumerable<DriveKey>), typeof(CancellationToken)], [.. methods[0].GetParameters().Select(parameter => parameter.ParameterType)]);
        Assert.AreEqual(typeof(Task<IReadOnlyList<DriveOperationResult>>), methods[0].ReturnType);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LifecycleVerbs_ReturnOneNormalizedResultPerRequestedKey(bool several)
    {
        using var harness = new WatchHarness('T', 'U');
        DriveKey[] requested = several ? ['u', 't'] : ['t'];
        var token = CancellationToken.None;
        var started = await harness.Index.StartWatchingAsync(requested, token);
        AssertResults(started, requested);
        var waiting = harness.Index.WaitForCatchUpAsync(requested, token);
        foreach (var key in requested)
        {
            await harness.Source.WatchFor(key.Letter).Publish(new DriveCaughtUp());
        }
        AssertResults(await waiting, requested);
        AssertResults(await harness.Index.RescanAsync(requested, token), requested);
        AssertResults(await harness.Index.StopWatchingAsync(requested, token), requested);
        Assert.AreSequenceEqual(['T', 'U'], [.. harness.Index.Drives.Select(status => status.Drive)]);
    }

    [TestMethod]
    public async Task LifecycleVerbs_RejectDefaultKeyBeforeAnyOperation()
    {
        using var harness = new WatchHarness('T');
        DriveKey[] requested = ['T', default];
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => harness.Index.StartWatchingAsync(requested, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => harness.Index.StopWatchingAsync(requested, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => harness.Index.WaitForCatchUpAsync(requested, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => harness.Index.RescanAsync(requested, CancellationToken.None));
        Assert.IsEmpty(harness.Source.Starts);
        Assert.AreEqual(1, harness.ProductionCount('T'));
    }

    [TestMethod]
    public async Task PublicDriveEntryPoints_RejectDefaultBeforeAccessingResources()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
        Assert.ThrowsExactly<ArgumentException>(() => index.Root(default, TestContext.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => index.ReadUsnJournalSettings(default));
        await using var session = BrokerTestHarness.CreateSession(_ => throw new AssertFailedException("the invalid key must not launch a broker"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            session.GrowUsnJournalAsync(default, new UsnJournalSettings { MaximumSize = 4096, AllocationDelta = 1024 }, CancellationToken.None));
        var source = new ScriptedWatchSource();
        Assert.ThrowsExactly<ArgumentException>(() => source.WatchFor(default));
        Assert.ThrowsExactly<ArgumentException>(() => source.WaitForStartAsync(default, 1, CancellationToken.None));
    }

    [TestMethod]
    public async Task LowerCaseRequests_ReturnNormalizedChangesAndFaults()
    {
        using var harness = new WatchHarness('t');
        harness.Index.HoldEveryRecovery();
        var changes = await harness.Index.ApplyJournalEntriesAsync('t', [WatchHarness.Create(9, "created.txt")],
            WatchHarness.JournalIdentifier, 200);
        Assert.AreEqual('T', changes.Single().Entry.RecordKey.Drive);
        Assert.AreEqual('T', harness.Index.Root('t', TestContext.CancellationToken).RecordKey.Drive);
        await harness.Index.StartWatchingAsync(['t'], CancellationToken.None);
        harness.Source.WatchFor('t').FailDrive(new IOException("scripted drive failure"));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Drive, 't');
        Assert.AreEqual('T', fault.Drive);
        Assert.AreEqual('T', harness.Source.Starts.Single().Drive);
        Assert.AreEqual('T', harness.Source.Watches.Single().Drive);
    }

    [TestMethod]
    public async Task LifecycleVerbs_MaterializeEachSequenceOnceAndAcceptEmptySequences()
    {
        using var harness = new WatchHarness(new ScriptedWatchSource { CatchUpOnStart = true }.IndexSource, 'T');
        AssertResults(await harness.Index.StartWatchingAsync(new SingleUseSequence('t'), CancellationToken.None), ['T']);
        AssertResults(await harness.Index.WaitForCatchUpAsync(new SingleUseSequence('t'), CancellationToken.None), ['T']);
        AssertResults(await harness.Index.RescanAsync(new SingleUseSequence('t'), CancellationToken.None), ['T']);
        AssertResults(await harness.Index.StopWatchingAsync(new SingleUseSequence('t'), CancellationToken.None), ['T']);
        Assert.IsEmpty(await harness.Index.StartWatchingAsync([], CancellationToken.None));
        Assert.IsEmpty(await harness.Index.WaitForCatchUpAsync([], CancellationToken.None));
        Assert.IsEmpty(await harness.Index.RescanAsync([], CancellationToken.None));
        Assert.IsEmpty(await harness.Index.StopWatchingAsync([], CancellationToken.None));
    }

    sealed class SingleUseSequence(DriveKey drive) : IEnumerable<DriveKey>
    {
        bool _enumerated;

        public IEnumerator<DriveKey> GetEnumerator()
        {
            Assert.IsFalse(_enumerated, "the request must be enumerated exactly once");
            _enumerated = true;
            yield return drive;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    static void AssertResults(IReadOnlyList<DriveOperationResult> results, DriveKey[] requested)
    {
        Assert.AreSequenceEqual(requested, [.. results.Select(result => result.Drive)]);
        Assert.IsTrue(results.All(result => result.Outcome == DriveOperationOutcome.Succeeded));
    }

    public TestContext TestContext { get; set; } = null!;
}
