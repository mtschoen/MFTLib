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
        Assert.AreEqual(1, tasks.Snapshot().Length);

        pending.SetResult();

        Assert.AreEqual(0, tasks.Snapshot().Length);
    }

    [TestMethod]
    public void Track_TaskAlreadyCompleted_IsNotRetained()
    {
        var tasks = new JournalBrokerHost.SessionTasks();

        tasks.Track(Task.CompletedTask);

        Assert.AreEqual(0, tasks.Snapshot().Length);
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

        Assert.AreEqual(pending.Length, tasks.Snapshot().Length);
        pending[10].SetResult();
        Assert.AreEqual(pending.Length - 1, tasks.Snapshot().Length);
        Assert.IsFalse(tasks.Snapshot().Contains(pending[10].Task));
    }

    [TestMethod]
    public async Task Drain_FaultedTask_IsRetainedAndRethrown()
    {
        var host = new JournalBrokerHost(_ => default, (_, _, _, _, _) => [], (_, since, _) => ([], since),
            timeProvider: new FakeTimeProvider());
        var tasks = new JournalBrokerHost.SessionTasks();
        var failure = new InvalidOperationException("request failed");
        tasks.Track(Task.FromException(failure));

        Assert.AreEqual(1, tasks.Snapshot().Length);
        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => host.DrainAsync(tasks));
        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    public void Track_CancelledTask_IsRetained()
    {
        var tasks = new JournalBrokerHost.SessionTasks();

        tasks.Track(Task.FromCanceled(new CancellationToken(canceled: true)));

        Assert.AreEqual(1, tasks.Snapshot().Length);
    }
}
