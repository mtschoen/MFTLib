using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.SampleHostContract;

partial class SharedSampleHostTests
{
    [TestMethod]
    public void Run_NotElevated_DialogStartedLateThenAnsweredAtOnce_IsAccidentalAndShownAgain()
    {
        var events = new List<string>();
        var scanner = Scanner([], events, []);
        var clock = scanner.Clock;
        // The dialog thread starts ten minutes late, as when the pool is busy. Only the time on screen counts.
        scanner.SetDialogScheduler(new LateStartScheduler(() => clock.Advance(TimeSpan.FromMinutes(10))));
        var answers = new Queue<(int Result, double Seconds)>([(scanner.ResultOk, 0), (scanner.ResultOk, 3)]);
        scanner.SetMessageBox((_, _, _, _) =>
        {
            events.Add("dialog");
            var answer = answers.Dequeue();
            clock.Advance(TimeSpan.FromSeconds(answer.Seconds));
            return answer.Result;
        });

        var result = RunElevation(scanner);

        Assert.AreEqual(0, result);
        Assert.AreSequenceEqual(["dialog", "dialog", "elevate"], events);
    }

    // The caller resumes only after the dialog thread is done, so an immediate answer followed by a long delay before
    // the caller runs must still be judged by the time the dialog was on screen, which was none.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Run_NotElevated_ImmediateAnswerThenALongCallerDelay_IsStillAccidentalAndShownAgain(bool cancel)
    {
        var events = new List<string>();
        var delayedCaller = new TaskCompletionSource();
        var scanner = Scanner([], events, []);
        var clock = scanner.Clock;
        var beepCount = 0;
        scanner.SetMessageBeep(_ =>
        {
            if (++beepCount > 1)
            {
                delayedCaller.Task.Wait(TestContext.CancellationToken);
            }

            return true;
        });
        var scheduler = new AfterRunScheduler(() =>
        {
            try
            {
                clock.Advance(TimeSpan.FromMinutes(10));
            }
            finally
            {
                delayedCaller.TrySetResult();
            }
        });
        scanner.SetDialogScheduler(scheduler);
        var answers = new Queue<(int Result, double Seconds)>([(cancel ? scanner.ResultCancel : scanner.ResultOk, 0), (scanner.ResultOk, 3)]);
        scanner.SetMessageBox((_, _, _, _) =>
        {
            events.Add("dialog");
            var answer = answers.Dequeue();
            clock.Advance(TimeSpan.FromSeconds(answer.Seconds));
            return answer.Result;
        });

        var result = RunElevation(scanner);
        scheduler.CheckForFailure();

        Assert.AreEqual(0, result);
        Assert.AreSequenceEqual(["dialog", "dialog", "elevate"], events);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Run_NotElevated_ImmediateAnswerThenForcedCallerDelayAtNextBeep_IsStillAccidentalAndShownAgain(bool cancel)
    {
        var events = new List<string>();
        var delayedCaller = new TaskCompletionSource();
        var callerReachedNextBeep = new TaskCompletionSource();
        var callerWaitingOnDelayedCaller = new TaskCompletionSource();
        var secondDialogStarted = new TaskCompletionSource();
        var hookFinishedAdvance = new TaskCompletionSource();
        var scanner = Scanner([], events, []);
        var clock = scanner.Clock;
        var beepCount = 0;
        scanner.SetMessageBeep(_ =>
        {
            if (++beepCount > 1)
            {
                callerReachedNextBeep.TrySetResult();
                callerWaitingOnDelayedCaller.TrySetResult();
                delayedCaller.Task.Wait(TestContext.CancellationToken);
            }

            return true;
        });
        var scheduler = new AfterRunScheduler(() =>
        {
            try
            {
                callerReachedNextBeep.Task.Wait(TestContext.CancellationToken);
                Task.WaitAny([callerWaitingOnDelayedCaller.Task, secondDialogStarted.Task], TestContext.CancellationToken);
                clock.Advance(TimeSpan.FromMinutes(10));
            }
            finally
            {
                delayedCaller.TrySetResult();
                hookFinishedAdvance.TrySetResult();
            }
        });
        scanner.SetDialogScheduler(scheduler);
        var answers = new Queue<(int Result, double Seconds)>([(cancel ? scanner.ResultCancel : scanner.ResultOk, 0), (scanner.ResultOk, 3)]);
        scanner.SetMessageBox((_, _, _, _) =>
        {
            events.Add("dialog");
            if (events.Count == 2)
            {
                secondDialogStarted.TrySetResult();
                hookFinishedAdvance.Task.Wait(TestContext.CancellationToken);
            }

            var answer = answers.Dequeue();
            clock.Advance(TimeSpan.FromSeconds(answer.Seconds));
            return answer.Result;
        });

        var result = RunElevation(scanner);
        scheduler.CheckForFailure();

        Assert.AreEqual(0, result);
        Assert.AreSequenceEqual(["dialog", "dialog", "elevate"], events);
    }

    [TestMethod]
    public void Run_NotElevated_OkReturnedAfterTheTimeoutButBothTasksComplete_IsNotAuthorization()
    {
        var lines = new List<string>();
        var events = new List<string>();
        var scanner = Scanner(lines, events, [Answer(Probe.ResultOk, Probe.NoticeTimeout.TotalSeconds + 1)]);

        var result = RunElevation(scanner);

        Assert.AreEqual(1, result);
        Assert.AreSequenceEqual(["dialog"], events);
        Assert.Contains(line => line.Contains("not answered in time"), lines);
    }

    [TestMethod]
    public void Run_NotElevated_AnswerExactlyAtTheTimeout_TimesOutAndOneTickEarlierStands()
    {
        var atTimeoutEvents = new List<string>();
        var atTimeout = Scanner([], atTimeoutEvents, [Answer(Probe.ResultOk, Probe.NoticeTimeout.TotalSeconds)]);
        var earlierEvents = new List<string>();
        var earlier = Scanner(
            [], earlierEvents,
            [Answer(Probe.ResultOk, Probe.NoticeTimeout.TotalSeconds - 0.001)]);

        Assert.AreEqual(1, RunElevation(atTimeout));
        Assert.AreEqual(0, RunElevation(earlier));

        Assert.AreSequenceEqual(["dialog"], atTimeoutEvents);
        Assert.AreSequenceEqual(["dialog", "elevate"], earlierEvents);
    }

    // Runs each queued task on a new thread, then a hook once the task has completed.
    sealed class AfterRunScheduler(Action afterRun) : TaskScheduler
    {
        readonly List<Thread> _threads = [];
        Exception? _failure;

        public void CheckForFailure()
        {
            lock (_threads)
            {
                foreach (var thread in _threads)
                {
                    thread.Join(TimeSpan.FromSeconds(5));
                }
            }

            if (_failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
            }
        }

        protected override void QueueTask(Task task)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    TryExecuteTask(task);
                    afterRun();
                }
                catch (OperationCanceledException)
                {
                    // Expected cancellation at the scheduler thread boundary.
                }
                catch (AggregateException exception) when (exception.InnerExceptions.All(e => e is OperationCanceledException))
                {
                    // Expected cancellation when waiting on tasks.
                }
                catch (Exception exception)
                {
                    _failure = exception;
                }
            });
            lock (_threads)
            {
                _threads.Add(thread);
            }

            thread.Start();
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
