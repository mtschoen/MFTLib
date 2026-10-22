using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class JournalBrokerHostSessionTasksTests
{
    [TestMethod]
    public void Track_TaskCompletesSuccessfully_IsForgottenWithoutAnotherTrack()
    {
        var tasks = new JournalBrokerHost.SessionTasks();
        var pending = new TaskCompletionSource();
        tasks.Track(pending.Task);
        Assert.HasCount(1, tasks.Snapshot());

        pending.SetResult();

        Assert.IsEmpty(tasks.Snapshot());
    }

    [TestMethod]
    public void Track_TaskAlreadyCompleted_IsNotRetained()
    {
        var tasks = new JournalBrokerHost.SessionTasks();

        tasks.Track(Task.CompletedTask);

        Assert.IsEmpty(tasks.Snapshot());
    }

    [TestMethod]
    public void Track_ManyPendingTasks_KeepsEveryOneUntilItCompletes()
    {
        var tasks = new JournalBrokerHost.SessionTasks();
        var pending = Enumerable.Range(0, 50).Select(_ => new TaskCompletionSource()).ToArray();
        foreach (var source in pending)
        {
            tasks.Track(source.Task);
        }

        Assert.HasCount(pending.Length, tasks.Snapshot());
        pending[10].SetResult();
        Assert.HasCount(pending.Length - 1, tasks.Snapshot());
        Assert.IsFalse(tasks.Snapshot().Contains(pending[10].Task));
    }

    [TestMethod]
    public async Task Drain_FaultedTask_IsRetainedAndRethrown()
    {
        var host = new JournalBrokerHost(
            new ScriptedVolumeAccess
            {
                CursorQuery = _ => default,
                RecordBatches = (_, _, _, _, _, _) => [],
                JournalRead = (_, since, _) => ([], since)
            },
            timeProvider: new FakeTimeProvider());
        var tasks = new JournalBrokerHost.SessionTasks();
        var failure = new InvalidOperationException("request failed");
        tasks.Track(Task.FromException(failure));

        Assert.HasCount(1, tasks.Snapshot());
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.DrainAsync(tasks));
        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    public void Track_CancelledTask_IsRetained()
    {
        var tasks = new JournalBrokerHost.SessionTasks();

        tasks.Track(Task.FromCanceled(new CancellationToken(canceled: true)));

        Assert.HasCount(1, tasks.Snapshot());
    }
}
