using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;

namespace MFTLib.Tests;

partial class SampleHostElevationNoticeTests
{
    [TestMethod]
    public void Run_NotElevated_DialogStartedLateThenAnsweredAtOnce_IsAccidentalAndShownAgain()
    {
        var events = new List<string>();
        var scanner = Scanner(new List<string>(), events, []);
        var clock = (FakeTimeProvider)scanner._timeProvider;
        // The dialog thread starts ten minutes late, as when the pool is busy. Only the time on screen counts.
        scanner._dialogScheduler = new LateStartScheduler(() => clock.Advance(TimeSpan.FromMinutes(10)));
        var answers = new Queue<(int Result, double Seconds)>([(SampleHost.MessageBoxResultOk, 0), (SampleHost.MessageBoxResultOk, 3)]);
        scanner._messageBox = (_, _, _, _) =>
        {
            events.Add("dialog");
            var answer = answers.Dequeue();
            clock.Advance(TimeSpan.FromSeconds(answer.Seconds));
            return answer.Result;
        };

        var result = RunElevation(scanner);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(new[] { "dialog", "dialog", "elevate" }, events);
    }

    // The caller resumes only after the dialog thread is done, so an immediate answer followed by a long delay before
    // the caller runs must still be judged by the time the dialog was on screen, which was none.
    [DataTestMethod]
    [DataRow(SampleHost.MessageBoxResultOk)]
    [DataRow(SampleHost.MessageBoxResultCancel)]
    public void Run_NotElevated_ImmediateAnswerThenALongCallerDelay_IsStillAccidentalAndShownAgain(int button)
    {
        var events = new List<string>();
        var delayedCaller = new TaskCompletionSource();
        var scanner = Scanner(new List<string>(), events, []);
        var clock = (FakeTimeProvider)scanner._timeProvider;
        var beepCount = 0;
        scanner._messageBeep = _ =>
        {
            if (++beepCount > 1)
            {
                delayedCaller.Task.Wait();
            }

            return true;
        };
        scanner._dialogScheduler = new AfterRunScheduler(() =>
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            delayedCaller.TrySetResult();
        });
        var answers = new Queue<(int Result, double Seconds)>([(button, 0), (SampleHost.MessageBoxResultOk, 3)]);
        scanner._messageBox = (_, _, _, _) =>
        {
            events.Add("dialog");
            var answer = answers.Dequeue();
            clock.Advance(TimeSpan.FromSeconds(answer.Seconds));
            return answer.Result;
        };

        var result = RunElevation(scanner);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(new[] { "dialog", "dialog", "elevate" }, events);
    }

    [DataTestMethod]
    [DataRow(SampleHost.MessageBoxResultOk)]
    [DataRow(SampleHost.MessageBoxResultCancel)]
    public void Run_NotElevated_ImmediateAnswerThenForcedCallerDelayAtNextBeep_IsStillAccidentalAndShownAgain(int button)
    {
        var events = new List<string>();
        var delayedCaller = new TaskCompletionSource();
        var callerReachedNextBeep = new TaskCompletionSource();
        var callerWaitingOnDelayedCaller = new TaskCompletionSource();
        var secondDialogStarted = new TaskCompletionSource();
        var hookFinishedAdvance = new TaskCompletionSource();
        var scanner = Scanner(new List<string>(), events, []);
        var clock = (FakeTimeProvider)scanner._timeProvider;
        var beepCount = 0;
        scanner._messageBeep = _ =>
        {
            if (++beepCount > 1)
            {
                callerReachedNextBeep.TrySetResult();
                callerWaitingOnDelayedCaller.TrySetResult();
                delayedCaller.Task.Wait();
            }

            return true;
        };
        scanner._dialogScheduler = new AfterRunScheduler(() =>
        {
            callerReachedNextBeep.Task.Wait();
            Task.WaitAny(callerWaitingOnDelayedCaller.Task, secondDialogStarted.Task);
            clock.Advance(TimeSpan.FromMinutes(10));
            delayedCaller.TrySetResult();
            hookFinishedAdvance.TrySetResult();
        });
        var answers = new Queue<(int Result, double Seconds)>([(button, 0), (SampleHost.MessageBoxResultOk, 3)]);
        scanner._messageBox = (_, _, _, _) =>
        {
            events.Add("dialog");
            if (events.Count == 2)
            {
                secondDialogStarted.TrySetResult();
                hookFinishedAdvance.Task.Wait();
            }

            var answer = answers.Dequeue();
            clock.Advance(TimeSpan.FromSeconds(answer.Seconds));
            return answer.Result;
        };

        var result = RunElevation(scanner);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(new[] { "dialog", "dialog", "elevate" }, events);
    }

    [TestMethod]
    public void Run_NotElevated_OkReturnedAfterTheTimeoutButBothTasksComplete_IsNotAuthorization()
    {
        var lines = new List<string>();
        var events = new List<string>();
        var scanner = Scanner(lines, events, [Answer(SampleHost.MessageBoxResultOk, SampleHost.ElevationNoticeTimeout.TotalSeconds + 1)]);

        var result = RunElevation(scanner);

        Assert.AreEqual(1, result);
        CollectionAssert.AreEqual(new[] { "dialog" }, events);
        Assert.IsTrue(lines.Any(line => line.Contains("not answered in time")));
    }

    [TestMethod]
    public void Run_NotElevated_AnswerExactlyAtTheTimeout_TimesOutAndOneTickEarlierStands()
    {
        var atTimeoutEvents = new List<string>();
        var atTimeout = Scanner(new List<string>(), atTimeoutEvents, [Answer(SampleHost.MessageBoxResultOk, SampleHost.ElevationNoticeTimeout.TotalSeconds)]);
        var earlierEvents = new List<string>();
        var earlier = Scanner(
            new List<string>(), earlierEvents,
            [Answer(SampleHost.MessageBoxResultOk, SampleHost.ElevationNoticeTimeout.TotalSeconds - 0.001)]);

        Assert.AreEqual(1, RunElevation(atTimeout));
        Assert.AreEqual(0, RunElevation(earlier));

        CollectionAssert.AreEqual(new[] { "dialog" }, atTimeoutEvents);
        CollectionAssert.AreEqual(new[] { "dialog", "elevate" }, earlierEvents);
    }

    // Runs each queued task on a new thread, then a hook once the task has completed.
    sealed class AfterRunScheduler(Action afterRun) : TaskScheduler
    {
        protected override void QueueTask(Task task)
        {
            new Thread(() =>
            {
                TryExecuteTask(task);
                afterRun();
            }).Start();
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }

    // Runs each queued task on a new thread after a hook, standing in for a thread pool that is slow to start work.
    sealed class LateStartScheduler(Action beforeStart) : TaskScheduler
    {
        protected override void QueueTask(Task task)
        {
            new Thread(() =>
            {
                beforeStart();
                TryExecuteTask(task);
            }).Start();
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }
}
