using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public class RootKeyLifecycleTests
{
    [TestMethod]
    [DataRow("StartWatchingAsync")]
    [DataRow("StopWatchingAsync")]
    [DataRow("WaitForCatchUpAsync")]
    [DataRow("RescanAsync")]
    public void LifecycleVerb_HasOneSingleRootMethod(string name)
    {
        var methods = typeof(FileIndex).GetMethods().Where(method => method.Name == name).ToArray();
        Assert.HasCount(1, methods);
        Assert.AreSequenceEqual([typeof(CancellationToken)], [.. methods[0].GetParameters().Select(parameter => parameter.ParameterType)]);
        Assert.AreEqual(typeof(Task<RootOperationResult>), methods[0].ReturnType);
    }

    [TestMethod]
    public async Task LifecycleVerbs_ReturnNormalizedRootKeyInResult()
    {
        using var harness = new WatchHarness('t');
        var token = CancellationToken.None;
        var started = await harness.Index.StartWatchingAsync(token);
        AssertResult(started, new VolumeKey('T'));
        var waiting = harness.Index.WaitForCatchUpAsync(token);
        await harness.Source.WatchFor(new VolumeKey('T')).Publish(new VolumeCaughtUp());
        AssertResult(await waiting, new VolumeKey('T'));
        AssertResult(await harness.Index.RescanAsync(token), new VolumeKey('T'));
        AssertResult(await harness.Index.StopWatchingAsync(token), new VolumeKey('T'));
        Assert.AreEqual(new VolumeKey('T'), harness.Index.Status.Root);
    }

    [TestMethod]
    public async Task PublicRootEntryPoints_RejectDefaultBeforeAccessingResources()
    {
        using var harness = new WatchHarness('T');
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
        harness.HoldEveryRecovery();
        var changes = await harness.Index.ApplyJournalEntriesAsync('t', [WatchHarness.Create(9, "created.txt")],
            WatchHarness.JournalIdentifier, 200);
        Assert.AreEqual(new VolumeKey('T'), changes.Single().Entry.RecordKey.Root);
        Assert.AreEqual(new VolumeKey('T'), harness.IndexFor('t').Root(TestContext.CancellationToken).RecordKey.Root);
        await harness.IndexFor('t').StartWatchingAsync(CancellationToken.None);
        harness.Source.WatchFor('t').FailVolume(new IOException("scripted root failure"));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Volume, 't');
        Assert.AreEqual('T', fault.Volume);
        Assert.AreEqual('T', harness.Source.Starts.Single().Volume);
        Assert.AreEqual('T', harness.Source.Watches.Single().Volume);
    }

    static void AssertResult(RootOperationResult result, RootKey expected)
    {
        Assert.AreEqual(expected, result.Root);
        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome);
    }

    public TestContext TestContext { get; set; } = null!;
}
