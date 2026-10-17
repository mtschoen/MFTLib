using System.Reflection;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerSessionTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Ended_PendingLaunchPublicationPreservesOnlyAnEndCompletedBeforeDisposal(
        bool endedBeforeDisposal)
    {
        await using var handle = StartBroker();
        var brokerProcess = handle.Process;
        var launch = new TaskCompletionSource<BrokerProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new QueuedTaskScheduler();
        var session = new BrokerSession(_ => launch.Task, scheduler);
        try
        {
            var connecting = session.ConnectAsync(CancellationToken.None);
            scheduler.RunPending();
            if (endedBeforeDisposal)
            {
                handle.Crash();
                await handle.Ended.WaitAsync(HangGuard);
            }

            var disposal = StartDisposalBeforePublication(session, launch, handle, endedBeforeDisposal);
            await Assert.ThrowsAsync<OperationCanceledException>(() => connecting)
                .WaitAsync(HangGuard);
            await disposal.WaitAsync(HangGuard);
            await session.DisposeAsync();

            Assert.AreEqual(endedBeforeDisposal, session.Ended.IsCompleted,
                "only a broker end completed before the first disposal must survive pending publication");
            if (endedBeforeDisposal)
            {
                Assert.AreEqual(await handle.Ended, await session.Ended);
            }
        }
        finally
        {
            launch.TrySetResult(brokerProcess);
            await session.DisposeAsync();
        }
    }

    static Task StartDisposalBeforePublication(BrokerSession session, TaskCompletionSource<BrokerProcess> launch,
        InProcessBrokerHandle handle, bool endedBeforeDisposal)
    {
        var hangGuard = HangGuard;
        // Hold the production gate so the completed launcher cannot publish ownership before disposal.
        var gate = (Lock)typeof(BrokerSession).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session)!;
        lock (gate)
        {
            launch.SetResult(handle.Process);
            Assert.IsTrue(launch.Task.IsCompletedSuccessfully);
            var disposal = session.DisposeAsync().AsTask();
            if (!endedBeforeDisposal)
            {
                handle.Crash();
                Assert.IsTrue(handle.Ended.Wait(hangGuard), "the in-process broker did not end");
            }

            return disposal;
        }
    }
}
