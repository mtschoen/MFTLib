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
        Assert.AreEqual(1, methods.Length);
        CollectionAssert.AreEqual(new[] { typeof(IEnumerable<DriveKey>), typeof(CancellationToken) },
            methods[0].GetParameters().Select(parameter => parameter.ParameterType).ToArray());
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
        CollectionAssert.AreEqual(new DriveKey[] { 'T', 'U' },
            harness.Index.Drives.Select(status => status.Drive).ToArray());
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
        Assert.AreEqual(0, harness.Source.Starts.Count);
        Assert.AreEqual(1, harness.ProductionCount('T'));
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task PublicDriveEntryPoints_RejectDefaultBeforeAccessingResources()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
        {
            using var harness = new WatchHarness('T');
            harness.Index.Root(default);
        });
        Assert.ThrowsExactly<ArgumentException>(() =>
        {
            using var harness = new WatchHarness('T');
            harness.Index.QueryUsnJournalSettings(default);
        });
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
        {
            await using var session = BrokerTestHarness.CreateSession(_ => throw new AssertFailedException("the invalid key must not launch a broker"));
            await session.GrowUsnJournalAsync(default, new UsnJournalSettings { MaximumSize = 4096, AllocationDelta = 1024 }, CancellationToken.None);
        });
        var source = new ScriptedWatchSource();
        Assert.ThrowsExactly<ArgumentException>(() => source.WatchFor(default));
        Assert.ThrowsExactly<ArgumentException>(() => source.WaitForStartAsync(default, 1, CancellationToken.None));
    }

    [TestMethod]
    public async Task LowerCaseRequests_ReturnNormalizedChangesAndFaults()
    {
        using var harness = new WatchHarness('t');
        harness.Index.HoldEveryRecovery();
        var changes = harness.Index.ApplyJournalEntries('t', [WatchHarness.Create(9, "created.txt")],
            WatchHarness.JournalIdentifier, 200);
        Assert.AreEqual('T', changes.Single().Entry.RecordKey.Drive);
        Assert.AreEqual('T', harness.Index.Root('t').RecordKey.Drive);
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
        Assert.AreEqual(0, (await harness.Index.StartWatchingAsync([], CancellationToken.None)).Count);
        Assert.AreEqual(0, (await harness.Index.WaitForCatchUpAsync([], CancellationToken.None)).Count);
        Assert.AreEqual(0, (await harness.Index.RescanAsync([], CancellationToken.None)).Count);
        Assert.AreEqual(0, (await harness.Index.StopWatchingAsync([], CancellationToken.None)).Count);
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
        CollectionAssert.AreEqual(requested, results.Select(result => result.Drive).ToArray());
        Assert.IsTrue(results.All(result => result.Outcome == DriveOperationOutcome.Succeeded));
    }
}
