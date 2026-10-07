using MFTLib.Index;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;

namespace MFTLib.Tests;

// The heads-up dialog before the elevated relaunch (a Direct local scan) or the broker launch (the shared flow with
// ElevationNeed.BrokerLaunch, which Watch uses and Direct never asks for). The dialog and
// the clock are scripted: each stubbed dialog answers after a chosen amount of fake time, so no test sleeps
// or reads the real clock.
[TestClass]
public class SampleHostElevationNoticeTests
{
    const string ProcessPath = @"C:\tools\SampleProgram.Watch.exe";

    /// <summary>
    ///     Makes the dialog answer with an OK after a deliberate pause, so a scanner that reaches it neither shows
    ///     a real window nor treats the answer as accidental.
    /// </summary>
    internal static void AcknowledgeDeliberately(SampleHost scanner)
    {
        var clock = new FakeTimeProvider();
        scanner._timeProvider = clock;
        scanner._getEnvironmentVariable = _ => null;
        scanner._messageBeep = _ => true;
        scanner._messageBox = (_, _, _, _) =>
        {
            clock.Advance(SampleHost.AccidentalDismissalInterval);
            return SampleHost.MessageBoxResultOk;
        };
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Run_NotElevated_DeliberateOk_ElevatesExactlyOnceAfterTheDialog(bool isScanDrive)
    {
        var events = new List<string>();
        var (scanner, args, launchEvent) = CreateScannerForMode(isScanDrive, new List<string>(), events, [Answer(SampleHost.MessageBoxResultOk, 2)]);

        var result = RunMode(scanner, isScanDrive, args, events);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(new[] { "dialog", launchEvent }, events);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Run_NotElevated_DeliberateCancel_SkipsElevationAndPrintsTheFallback(bool isScanDrive)
    {
        var lines = new List<string>();
        var events = new List<string>();
        var (scanner, args, _) = CreateScannerForMode(isScanDrive, lines, events, [Answer(SampleHost.MessageBoxResultCancel, 2)]);

        var result = RunMode(scanner, isScanDrive, args, events);

        Assert.AreEqual(1, result);
        CollectionAssert.AreEqual(new[] { "dialog" }, events);
        Assert.IsTrue(lines.Any(line => line.Contains("AUTOMATIC ELEVATION FAILED", StringComparison.Ordinal) || line.Contains("AUTOMATIC ELEVATION FAILED")));
        Assert.IsTrue(lines.Any(line => line.Contains(ProcessPath, StringComparison.Ordinal) || line.Contains(ProcessPath)));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Run_NotElevated_TooFastOk_ShowsAgainAndElevatesOnlyAfterADeliberateAnswer(bool isScanDrive)
    {
        var events = new List<string>();
        var messages = new List<string>();
        var (scanner, args, launchEvent) = CreateScannerForMode(
            isScanDrive,
            new List<string>(),
            events,
            [Answer(SampleHost.MessageBoxResultOk, 0.2), Answer(SampleHost.MessageBoxResultOk, 3)],
            messages);

        var result = RunMode(scanner, isScanDrive, args, events);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(new[] { "dialog", "dialog", launchEvent }, events);
        Assert.IsFalse(messages[0].Contains("too fast", StringComparison.Ordinal));
        Assert.IsTrue(messages[1].Contains("too fast", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Run_NotElevated_TooFastCancel_ShowsAgainRatherThanCancelling()
    {
        var events = new List<string>();
        var scanner = Scanner(
            new List<string>(),
            events,
            [Answer(SampleHost.MessageBoxResultCancel, 0.2), Answer(SampleHost.MessageBoxResultOk, 3)]);

        var result = scanner.Run(["scan", "C"]);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(new[] { "dialog", "dialog", "elevate" }, events);
    }

    [TestMethod]
    public void Run_NotElevated_DismissalExactlyAtTheGuardInterval_IsDeliberate()
    {
        var events = new List<string>();
        var scanner = Scanner(
            new List<string>(), events, [Answer(SampleHost.MessageBoxResultOk, SampleHost.AccidentalDismissalInterval.TotalSeconds)]);

        scanner.Run(["scan", "C"]);

        CollectionAssert.AreEqual(new[] { "dialog", "elevate" }, events);
    }

    [TestMethod]
    public void Run_NotElevated_DialogFailure_DoesNotElevateOrLoop()
    {
        var lines = new List<string>();
        var events = new List<string>();
        var scanner = Scanner(lines, events, [Answer(0, 0)]);

        var result = scanner.Run(["scan", "C"]);

        Assert.AreEqual(1, result);
        CollectionAssert.AreEqual(new[] { "dialog" }, events);
        Assert.IsTrue(lines.Any(line => line.Contains("AUTOMATIC ELEVATION FAILED")));
    }

    [TestMethod]
    public void Run_Elevated_ShowsNoDialog()
    {
        var events = new List<string>();
        var scanner = Scanner(new List<string>(), events, []);
        scanner._isElevated = () => true;
        scanner._acrtIobFunc = _ => IntPtr.Zero;
        scanner._wFreopen = (_, _, _) => IntPtr.Zero;
        scanner._createSource = _ => throw new IOException("no scan in this test");

        scanner.Run(["scan", "T"]);

        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public void Run_NotElevated_CannotSelfElevate_ShowsNoDialog()
    {
        var events = new List<string>();
        var scanner = Scanner(new List<string>(), events, []);
        scanner._canSelfElevate = () => false;

        var result = scanner.Run(["scan", "C"]);

        Assert.AreEqual(1, result);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public void Run_NotElevated_DialogCarriesTheContractedTextAndFlags()
    {
        string? text = null;
        string? title = null;
        uint? type = null;
        uint? beep = null;
        var clock = new FakeTimeProvider();
        var scanner = new SampleHost
        {
            _isWindows = () => true,
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _tryRunElevated = (_, _) => true,
            _getProcessPath = () => ProcessPath,
            _writeLine = _ => { },
            _timeProvider = clock,
            _getEnvironmentVariable = _ => null,
            _messageBeep = sound =>
            {
                beep = sound;
                return true;
            },
            _messageBox = (_, body, caption, flags) =>
            {
                text = body;
                title = caption;
                type = flags;
                clock.Advance(TimeSpan.FromSeconds(5));
                return SampleHost.MessageBoxResultOk;
            }
        };

        scanner.Run(["search", "C", "--name", "", "--under", "a b&c"]);

        Assert.AreEqual(SampleHost.MessageBeepIconExclamation, beep);
        Assert.IsTrue(title!.Contains("SampleProgram", StringComparison.Ordinal));
        const uint expectedFlags = SampleHost.MessageBoxOkCancel | SampleHost.MessageBoxIconInformation
            | SampleHost.MessageBoxSystemModal | SampleHost.MessageBoxSetForeground | SampleHost.MessageBoxTopMost;
        Assert.AreEqual(expectedFlags, type);
        Assert.IsTrue(text!.Contains("UAC", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("administrator rights", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains($"Executable: {ProcessPath}", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("Arguments (6)", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("  search", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("  <empty>", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("  a b&c", StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Run_NotElevated_DialogNeverAnswered_TimesOutAsCancelWithoutElevating(bool isScanDrive)
    {
        var lines = new List<string>();
        var events = new List<string>();
        var (scanner, args, _) = CreateScannerForMode(isScanDrive, lines, events, []);
        var clock = new FakeTimeProvider();
        var neverAnswered = new TaskCompletionSource();
        scanner._timeProvider = clock;
        scanner._messageBox = (_, _, _, _) =>
        {
            events.Add("dialog");
            // The abandoned dialog is still open when the wait runs out: advance the fake clock past it and hang.
            clock.Advance(SampleHost.ElevationNoticeTimeout);
            // Runs on the dialog's own pool thread, which exists only to block here until the test releases it.
            neverAnswered.Task.Wait();
            return SampleHost.MessageBoxResultOk;
        };

        int result;
        try
        {
            result = RunMode(scanner, isScanDrive, args, events);
        }
        finally
        {
            neverAnswered.SetResult();
        }

        Assert.AreEqual(1, result);
        CollectionAssert.AreEqual(new[] { "dialog" }, events);
        Assert.IsTrue(lines.Any(line => line.Contains("not answered in time", StringComparison.Ordinal) || line.Contains("not answered in time")));
        Assert.IsTrue(lines.Any(line => line.Contains("AUTOMATIC ELEVATION FAILED", StringComparison.Ordinal) || line.Contains("AUTOMATIC ELEVATION FAILED")));
    }

    [TestMethod]
    public void Run_NotElevated_Unattended_SkipsTheDialogAndElevationAndPrintsTheFallback()
    {
        var lines = new List<string>();
        var events = new List<string>();
        var scanner = Scanner(lines, events, []);
        scanner._getEnvironmentVariable = Unattended;

        var result = scanner.Run(["scan", "C"]);

        Assert.AreEqual(1, result);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
        Assert.IsTrue(lines.Any(line => line.Contains("Running unattended (MFTLIB_SAMPLE_UNATTENDED=1): elevation skipped")));
        Assert.IsTrue(lines.Any(line => line.Contains("AUTOMATIC ELEVATION FAILED")));
        Assert.IsTrue(lines.Contains("  scan"));
    }

    [TestMethod]
    public void Run_LocalScan_Unattended_NeverScans()
    {
        var lines = new List<string>();
        var scanner = Scanner(lines, [], []);
        scanner._getEnvironmentVariable = Unattended;
        scanner._createSource = _ => throw new AssertFailedException("An unattended local scan must not run.");

        var result = scanner.Run(["scan", "C"]);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.Contains("Running unattended")));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("0")]
    [DataRow("true")]
    public void Run_NotElevated_SwitchNotSetToOne_ShowsTheDialog(string? value)
    {
        var events = new List<string>();
        var scanner = Scanner(new List<string>(), events, [Answer(SampleHost.MessageBoxResultOk, 2)]);
        scanner._getEnvironmentVariable = _ => value;

        scanner.Run(["scan", "C"]);

        CollectionAssert.AreEqual(new[] { "dialog", "elevate" }, events);
    }

    [TestMethod]
    public void Run_Elevated_IgnoresTheUnattendedSwitch()
    {
        var lines = new List<string>();
        var scanner = Scanner(lines, [], []);
        scanner._isElevated = () => true;
        scanner._acrtIobFunc = _ => IntPtr.Zero;
        scanner._wFreopen = (_, _, _) => IntPtr.Zero;
        scanner._getEnvironmentVariable = Unattended;
        scanner._createSource = _ => throw new IOException("no scan in this test");

        var result = scanner.Run(["scan", "T"]);

        Assert.AreEqual(1, result, "The scan was attempted and failed; the switch changed nothing.");
        Assert.IsFalse(lines.Any(line => line.Contains("Running unattended")));
    }

    static string? Unattended(string name)
    {
        return name == SampleHost.UnattendedVariableName ? "1" : null;
    }

    [TestMethod]
    public void Run_NotElevated_DialogStartedLateThenAnsweredAtOnce_IsAccidentalAndShownAgain()
    {
        var events = new List<string>();
        var clock = new FakeTimeProvider();
        var scanner = Scanner(new List<string>(), events, []);
        scanner._timeProvider = clock;
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

        var result = scanner.Run(["scan", "C"]);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(new[] { "dialog", "dialog", "elevate" }, events);
    }

    [TestMethod]
    public void Run_LocalScan_UnattendedButAlreadyElevated_ScansAsUsual()
    {
        var lines = new List<string>();
        var scanner = Scanner(lines, [], []);
        scanner._isElevated = () => true;
        scanner._acrtIobFunc = _ => IntPtr.Zero;
        scanner._wFreopen = (_, _, _) => IntPtr.Zero;
        scanner._getEnvironmentVariable = Unattended;
        scanner._createSource = _ => throw new IOException("the scan was attempted");

        var result = scanner.Run(["scan", "C"]);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.StartsWith("Error: ", StringComparison.Ordinal) && line.Contains("the scan was attempted")));
        Assert.IsFalse(lines.Any(line => line.Contains("Running unattended")));
    }





    [TestMethod]
    public void RunWithElevation_BrokerLaunch_NotElevated_DialogNamesTheBrokerLaunchItPrecedes()
    {
        string? text = null;
        string? title = null;
        var clock = new FakeTimeProvider();
        var scanner = new SampleHost
        {
            _isElevated = () => false,
            _getEnvironmentVariable = _ => null,
            _getProcessPath = () => ProcessPath,
            _writeLine = _ => { },
            _timeProvider = clock,
            _messageBeep = _ => true,
            _messageBox = (_, body, caption, _) =>
            {
                text = body;
                title = caption;
                clock.Advance(TimeSpan.FromSeconds(5));
                return SampleHost.MessageBoxResultOk;
            },
            _canSelfElevate = () => throw new AssertFailedException("A broker launch must not self-elevate."),
            _tryRunElevated = (_, _) => throw new AssertFailedException("A broker launch must not self-elevate.")
        };

        var result = scanner.RunWithElevation(["scan-drive", "C"], ElevationNeed.BrokerLaunch, () => 0);

        Assert.AreEqual(0, result);
        Assert.IsTrue(title!.Contains("SampleProgram", StringComparison.Ordinal));
        Assert.IsTrue(text!.Contains("UAC", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("administrator rights", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("launches its broker elevated", StringComparison.Ordinal));
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
        var clock = new CallerGatedClock(Environment.CurrentManagedThreadId, delayedCaller);
        var scanner = Scanner(new List<string>(), events, []);
        scanner._timeProvider = clock;
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

        var result = scanner.Run(["scan", "C"]);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(new[] { "dialog", "dialog", "elevate" }, events);
    }

    [TestMethod]
    public void Run_NotElevated_OkReturnedAfterTheTimeoutButBothTasksComplete_IsNotAuthorization()
    {
        var lines = new List<string>();
        var events = new List<string>();
        var scanner = Scanner(lines, events, [Answer(SampleHost.MessageBoxResultOk, SampleHost.ElevationNoticeTimeout.TotalSeconds + 1)]);

        var result = scanner.Run(["scan", "C"]);

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

        Assert.AreEqual(1, atTimeout.Run(["scan", "C"]));
        Assert.AreEqual(0, earlier.Run(["scan", "C"]));

        CollectionAssert.AreEqual(new[] { "dialog" }, atTimeoutEvents);
        CollectionAssert.AreEqual(new[] { "dialog", "elevate" }, earlierEvents);
    }

    // Reading the time on the caller's thread waits for the release, which comes only after the dialog thread has
    // finished and the clock has moved on, so a caller that reads the clock would see the delayed time.
    sealed class CallerGatedClock(int callerThread, TaskCompletionSource release) : FakeTimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            if (Environment.CurrentManagedThreadId == callerThread)
            {
                // Blocks only a caller that reads the clock, which is the behavior under test.
                release.Task.Wait();
            }

            return base.GetUtcNow();
        }
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

    static (SampleHost Scanner, string[] Arguments, string LaunchEvent) CreateScannerForMode(
        bool isScanDrive,
        List<string> lines,
        List<string> events,
        (int Result, double SecondsToAnswer)[] answers,
        List<string>? messages = null)
    {
        var scanner = Scanner(lines, events, answers, messages);
        if (isScanDrive)
        {
            scanner._canSelfElevate = () => throw new AssertFailedException("A broker launch must not self-elevate.");
            scanner._tryRunElevated = (_, _) => throw new AssertFailedException("A broker launch must not self-elevate.");
        }

        string[] args = isScanDrive ? ["scan-drive", "C"] : ["scan", "C"];
        var launchEvent = isScanDrive ? "create-session" : "elevate";
        return (scanner, args, launchEvent);
    }

    // The broker-launch path is the shared flow asked for ElevationNeed.BrokerLaunch, as the Watch sample asks for it.
    static int RunMode(SampleHost scanner, bool isScanDrive, string[] args, List<string> events)
    {
        return isScanDrive
            ? scanner.RunWithElevation(args, ElevationNeed.BrokerLaunch, () =>
            {
                events.Add("create-session");
                return 0;
            })
            : scanner.Run(args);
    }

    static (int Result, double SecondsToAnswer) Answer(int result, double secondsToAnswer) => (result, secondsToAnswer);

    static SampleHost Scanner(
        List<string> lines,
        List<string> events,
        (int Result, double SecondsToAnswer)[] answers,
        List<string>? messages = null)
    {
        var clock = new FakeTimeProvider();
        var next = 0;
        // The scanner of a run that must itself be elevated; the scan-drive tests turn that off, as the real scan is.
        return new SampleHost
        {
            _isWindows = () => true,
            _resolveDrive = letter => new IndexedDrive(char.ToUpperInvariant(letter[0]), Path.GetTempPath(), 1),
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _getProcessPath = () => ProcessPath,
            _writeLine = lines.Add,
            _timeProvider = clock,
            _getEnvironmentVariable = _ => null,
            _messageBeep = _ => true,
            _messageBox = (_, text, _, _) =>
            {
                events.Add("dialog");
                messages?.Add(text);
                var answer = answers[next++];
                clock.Advance(TimeSpan.FromSeconds(answer.SecondsToAnswer));
                return answer.Result;
            },
            _tryRunElevated = (_, _) =>
            {
                events.Add("elevate");
                return true;
            }
        };
    }

}
