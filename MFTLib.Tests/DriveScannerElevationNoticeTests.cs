using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestProgram;

namespace MFTLib.Tests;

// The heads-up dialog before the elevated relaunch or the broker launch of a scan-drive run. The dialog and
// the clock are scripted: each stubbed dialog answers after a chosen amount of fake time, so no test sleeps
// or reads the real clock.
[TestClass]
public class DriveScannerElevationNoticeTests
{
    const string ProcessPath = @"C:\tools\TestProgram.exe";

    /// <summary>
    ///     Makes the dialog answer with an OK after a deliberate pause, so a scanner that reaches it neither shows
    ///     a real window nor treats the answer as accidental.
    /// </summary>
    internal static void AcknowledgeDeliberately(DriveScanner scanner)
    {
        var clock = new FakeTimeProvider();
        scanner._timeProvider = clock;
        scanner._getEnvironmentVariable = _ => null;
        scanner._messageBeep = _ => true;
        scanner._messageBox = (_, _, _, _) =>
        {
            clock.Advance(DriveScanner.AccidentalDismissalInterval);
            return DriveScanner.MessageBoxResultOk;
        };
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Run_NotElevated_DeliberateOk_ElevatesExactlyOnceAfterTheDialog(bool isScanDrive)
    {
        var events = new List<string>();
        var (scanner, args, launchEvent) = CreateScannerForMode(isScanDrive, new List<string>(), events, [Answer(DriveScanner.MessageBoxResultOk, 2)]);

        var result = scanner.Run(args);

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
        var (scanner, args, _) = CreateScannerForMode(isScanDrive, lines, events, [Answer(DriveScanner.MessageBoxResultCancel, 2)]);

        var result = scanner.Run(args);

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
            [Answer(DriveScanner.MessageBoxResultOk, 0.2), Answer(DriveScanner.MessageBoxResultOk, 3)],
            messages);

        var result = scanner.Run(args);

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
            [Answer(DriveScanner.MessageBoxResultCancel, 0.2), Answer(DriveScanner.MessageBoxResultOk, 3)]);

        var result = scanner.Run(["C"]);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(new[] { "dialog", "dialog", "elevate" }, events);
    }

    [TestMethod]
    public void Run_NotElevated_DismissalExactlyAtTheGuardInterval_IsDeliberate()
    {
        var events = new List<string>();
        var scanner = Scanner(
            new List<string>(), events, [Answer(DriveScanner.MessageBoxResultOk, DriveScanner.AccidentalDismissalInterval.TotalSeconds)]);

        scanner.Run(["C"]);

        CollectionAssert.AreEqual(new[] { "dialog", "elevate" }, events);
    }

    [TestMethod]
    public void Run_NotElevated_DialogFailure_DoesNotElevateOrLoop()
    {
        var lines = new List<string>();
        var events = new List<string>();
        var scanner = Scanner(lines, events, [Answer(0, 0)]);

        var result = scanner.Run(["C"]);

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

        scanner.Run(["parse-file", "missing.bin"]);

        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public void Run_NotElevated_CannotSelfElevate_ShowsNoDialog()
    {
        var events = new List<string>();
        var scanner = Scanner(new List<string>(), events, []);
        scanner._canSelfElevate = () => false;

        var result = scanner.Run(["C"]);

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
        var scanner = new DriveScanner
        {
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
                return DriveScanner.MessageBoxResultOk;
            }
        };

        scanner.Run(["find-name", "C", "--name", "", "--name", "a b&c"]);

        Assert.AreEqual(DriveScanner.MessageBeepIconExclamation, beep);
        Assert.IsTrue(title!.Contains("TestProgram", StringComparison.Ordinal));
        const uint expectedFlags = DriveScanner.MessageBoxOkCancel | DriveScanner.MessageBoxIconInformation
            | DriveScanner.MessageBoxSystemModal | DriveScanner.MessageBoxSetForeground | DriveScanner.MessageBoxTopMost;
        Assert.AreEqual(expectedFlags, type);
        Assert.IsTrue(text!.Contains("UAC", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("administrator rights", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains($"Executable: {ProcessPath}", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("Arguments (6)", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("  find-name", StringComparison.Ordinal));
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
            clock.Advance(DriveScanner.ElevationNoticeTimeout);
            // Runs on the dialog's own pool thread, which exists only to block here until the test releases it.
            neverAnswered.Task.Wait();
            return DriveScanner.MessageBoxResultOk;
        };

        int result;
        try
        {
            result = scanner.Run(args);
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

        var result = scanner.Run(["find-name", "C", "--name", "x"]);

        Assert.AreEqual(1, result);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
        Assert.IsTrue(lines.Any(line => line.Contains("Running unattended (MFTLIB_TESTPROGRAM_UNATTENDED=1): elevation skipped")));
        Assert.IsTrue(lines.Any(line => line.Contains("AUTOMATIC ELEVATION FAILED")));
        Assert.IsTrue(lines.Contains("  find-name"));
    }

    [TestMethod]
    public void Run_ScanDrive_Unattended_NeverLaunchesTheBroker()
    {
        var lines = new List<string>();
        var scanner = Scanner(lines, [], []);
        scanner._getEnvironmentVariable = Unattended;
        scanner._createBrokerSession = () => throw new AssertFailedException("Unattended scan-drive must not launch the broker.");

        var result = scanner.Run(["scan-drive", "C"]);

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
        var scanner = Scanner(new List<string>(), events, [Answer(DriveScanner.MessageBoxResultOk, 2)]);
        scanner._getEnvironmentVariable = _ => value;

        scanner.Run(["C"]);

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
        scanner._openVolume = _ => throw new IOException("no volume in this test");
        scanner._openVolumeWithBuffer = (_, _) => throw new IOException("no volume in this test");

        var result = scanner.Run(["find-name", "T", "--name", "x"]);

        Assert.AreEqual(0, result);
        Assert.IsFalse(lines.Any(line => line.Contains("Running unattended")));
    }

    static string? Unattended(string name)
    {
        return name == DriveScanner.UnattendedVariableName ? "1" : null;
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
        var answers = new Queue<(int Result, double Seconds)>([(DriveScanner.MessageBoxResultOk, 0), (DriveScanner.MessageBoxResultOk, 3)]);
        scanner._messageBox = (_, _, _, _) =>
        {
            events.Add("dialog");
            var answer = answers.Dequeue();
            clock.Advance(TimeSpan.FromSeconds(answer.Seconds));
            return answer.Result;
        };

        var result = scanner.Run(["C"]);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(new[] { "dialog", "dialog", "elevate" }, events);
    }

    [TestMethod]
    public void Run_ScanDrive_UnattendedButAlreadyElevated_ScansAsUsual()
    {
        var lines = new List<string>();
        var scanner = Scanner(lines, [], []);
        scanner._isElevated = () => true;
        scanner._getEnvironmentVariable = Unattended;
        scanner._createBrokerSession = () => throw new IOException("the scan was attempted");

        var result = scanner.Run(["scan-drive", "C"]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(lines.Contains("Error creating broker session: the scan was attempted"));
        Assert.IsFalse(lines.Any(line => line.Contains("Running unattended")));
    }





    [TestMethod]
    public void Run_ScanDrive_NotElevated_DialogNamesTheBrokerLaunchItPrecedes()
    {
        string? text = null;
        string? title = null;
        var clock = new FakeTimeProvider();
        var scanner = new DriveScanner
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
                return DriveScanner.MessageBoxResultOk;
            },
            _createBrokerSession = () => throw new IOException("the scripted launch stands in for the broker"),
            _canSelfElevate = () => throw new AssertFailedException("scan-drive must not self-elevate."),
            _tryRunElevated = (_, _) => throw new AssertFailedException("scan-drive must not self-elevate.")
        };

        var result = scanner.Run(["scan-drive", "C"]);

        Assert.AreEqual(0, result);
        Assert.IsTrue(title!.Contains("TestProgram", StringComparison.Ordinal));
        Assert.IsTrue(text!.Contains("UAC", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("administrator rights", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("launches its broker elevated", StringComparison.Ordinal));
    }

    // The caller resumes only after the dialog thread is done, so an immediate answer followed by a long delay before
    // the caller runs must still be judged by the time the dialog was on screen, which was none.
    [DataTestMethod]
    [DataRow(DriveScanner.MessageBoxResultOk)]
    [DataRow(DriveScanner.MessageBoxResultCancel)]
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
        var answers = new Queue<(int Result, double Seconds)>([(button, 0), (DriveScanner.MessageBoxResultOk, 3)]);
        scanner._messageBox = (_, _, _, _) =>
        {
            events.Add("dialog");
            var answer = answers.Dequeue();
            clock.Advance(TimeSpan.FromSeconds(answer.Seconds));
            return answer.Result;
        };

        var result = scanner.Run(["C"]);

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(new[] { "dialog", "dialog", "elevate" }, events);
    }

    [TestMethod]
    public void Run_NotElevated_OkReturnedAfterTheTimeoutButBothTasksComplete_IsNotAuthorization()
    {
        var lines = new List<string>();
        var events = new List<string>();
        var scanner = Scanner(lines, events, [Answer(DriveScanner.MessageBoxResultOk, DriveScanner.ElevationNoticeTimeout.TotalSeconds + 1)]);

        var result = scanner.Run(["C"]);

        Assert.AreEqual(1, result);
        CollectionAssert.AreEqual(new[] { "dialog" }, events);
        Assert.IsTrue(lines.Any(line => line.Contains("not answered in time")));
    }

    [TestMethod]
    public void Run_NotElevated_AnswerExactlyAtTheTimeout_TimesOutAndOneTickEarlierStands()
    {
        var atTimeoutEvents = new List<string>();
        var atTimeout = Scanner(new List<string>(), atTimeoutEvents, [Answer(DriveScanner.MessageBoxResultOk, DriveScanner.ElevationNoticeTimeout.TotalSeconds)]);
        var earlierEvents = new List<string>();
        var earlier = Scanner(
            new List<string>(), earlierEvents,
            [Answer(DriveScanner.MessageBoxResultOk, DriveScanner.ElevationNoticeTimeout.TotalSeconds - 0.001)]);

        Assert.AreEqual(1, atTimeout.Run(["C"]));
        Assert.AreEqual(0, earlier.Run(["C"]));

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

    static (DriveScanner Scanner, string[] Arguments, string LaunchEvent) CreateScannerForMode(
        bool isScanDrive,
        List<string> lines,
        List<string> events,
        (int Result, double SecondsToAnswer)[] answers,
        List<string>? messages = null)
    {
        var scanner = Scanner(lines, events, answers, messages);
        if (isScanDrive)
        {
            scanner._canSelfElevate = () => throw new AssertFailedException("scan-drive must not self-elevate.");
            scanner._tryRunElevated = (_, _) => throw new AssertFailedException("scan-drive must not self-elevate.");
            scanner._createBrokerSession = () =>
            {
                events.Add("create-session");
                throw new IOException("the scripted launch stands in for the broker");
            };
        }
        string[] args = isScanDrive ? ["scan-drive", "C"] : ["find-name", "C", "--name", "x"];
        var launchEvent = isScanDrive ? "create-session" : "elevate";
        return (scanner, args, launchEvent);
    }

    static (int Result, double SecondsToAnswer) Answer(int result, double secondsToAnswer) => (result, secondsToAnswer);

    static DriveScanner Scanner(
        List<string> lines,
        List<string> events,
        (int Result, double SecondsToAnswer)[] answers,
        List<string>? messages = null)
    {
        var clock = new FakeTimeProvider();
        var next = 0;
        return new DriveScanner
        {
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
