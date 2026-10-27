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
        Assert.AreEqual(typeof(Task<DriveOperationResult>), methods[0].ReturnType);
    }

    [TestMethod]
    public async Task LifecycleVerbs_ReturnNormalizedRootKeyInResult()
    {
        using var harness = new WatchHarness('t');
        var token = CancellationToken.None;
        var started = await harness.Index.StartWatchingAsync(token);
        AssertResult(started, new VolumeKey('T'));
        var waiting = harness.Index.WaitForCatchUpAsync(token);
        await harness.Source.WatchFor(new VolumeKey('T')).Publish(new DriveCaughtUp());
        AssertResult(await waiting, new VolumeKey('T'));
        AssertResult(await harness.Index.RescanAsync(token), new VolumeKey('T'));
        AssertResult(await harness.Index.StopWatchingAsync(token), new VolumeKey('T'));
        Assert.AreEqual(new VolumeKey('T'), harness.Index.Status.Drive);
    }

    [TestMethod]
    public async Task PublicDriveEntryPoints_RejectDefaultBeforeAccessingResources()
    {
        using var harness = new WatchHarness('T');
        var index = harness.Index;
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
        harness.HoldEveryRecovery();
        var changes = await harness.Index.ApplyJournalEntriesAsync('t', [WatchHarness.Create(9, "created.txt")],
            WatchHarness.JournalIdentifier, 200);
        Assert.AreEqual(new VolumeKey('T'), changes.Single().Entry.RecordKey.Drive);
        Assert.AreEqual(new VolumeKey('T'), harness.IndexFor('t').Root(TestContext.CancellationToken).RecordKey.Drive);
        await harness.IndexFor('t').StartWatchingAsync(CancellationToken.None);
        harness.Source.WatchFor('t').FailDrive(new IOException("scripted drive failure"));
        var fault = await harness.WaitForFaultAsync(WatchFaultKind.Drive, 't');
        Assert.AreEqual('T', fault.Drive);
        Assert.AreEqual('T', harness.Source.Starts.Single().Drive);
        Assert.AreEqual('T', harness.Source.Watches.Single().Drive);
    }

    static void AssertResult(DriveOperationResult result, RootKey expected)
    {
        Assert.AreEqual(expected, result.Drive);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, result.Outcome);
    }

    public TestContext TestContext { get; set; } = null!;
}
