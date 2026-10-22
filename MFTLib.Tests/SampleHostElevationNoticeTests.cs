using MFTLib.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;

namespace MFTLib.Tests;

// The heads-up dialog before the elevated relaunch (a Direct local scan). The dialog and the clock are scripted:
// each stubbed dialog answers after a chosen amount of fake time, so no test sleeps or reads the real clock.
[TestClass]
[DoNotParallelize]
public partial class SampleHostElevationNoticeTests
{
    string _directory = null!;
    string? _previousUnattended;

    [TestInitialize]
    public void SetUp()
    {
        _directory = Directory.CreateTempSubdirectory("notice-tests-").FullName;
        _previousUnattended = Environment.GetEnvironmentVariable(SampleHost.UnattendedVariableName);
        Environment.SetEnvironmentVariable(SampleHost.UnattendedVariableName, null);
    }

    [TestCleanup]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(SampleHost.UnattendedVariableName, _previousUnattended);
        Directory.Delete(_directory, true);
    }

    static int RunElevation(SampleHost scanner, string[]? arguments = null) =>
        scanner.RunWithElevation(arguments ?? ["scan", "C"], ElevationNeed.SelfElevate, () => 0);

    /// <summary>
    ///     Makes the dialog answer with an OK after a deliberate pause, so a scanner that reaches it neither shows
    ///     a real window nor treats the answer as accidental.
    /// </summary>
    internal static void AcknowledgeDeliberately(SampleHost scanner)
    {
        var clock = (FakeTimeProvider)scanner._timeProvider;
        scanner._messageBeep = _ => true;
        scanner._messageBox = (_, _, _, _) =>
        {
            clock.Advance(SampleHost.AccidentalDismissalInterval);
            return SampleHost.MessageBoxResultOk;
        };
    }

    [TestMethod]
    public void Run_NotElevated_DeliberateOk_ElevatesExactlyOnceAfterTheDialog()
    {
        var events = new List<string>();
        var scanner = Scanner([], events, [Answer(SampleHost.MessageBoxResultOk, 2)]);

        var result = RunElevation(scanner);

        Assert.AreEqual(0, result);
        Assert.AreSequenceEqual(["dialog", "elevate"], events);
    }

    [TestMethod]
    public void Run_NotElevated_DeliberateCancel_SkipsElevationAndPrintsTheFallback()
    {
        var lines = new List<string>();
        var events = new List<string>();
        var scanner = Scanner(lines, events, [Answer(SampleHost.MessageBoxResultCancel, 2)]);

        var result = RunElevation(scanner);

        Assert.AreEqual(1, result);
        Assert.AreSequenceEqual(["dialog"], events);
        Assert.Contains(line => line.Contains("AUTOMATIC ELEVATION FAILED", StringComparison.Ordinal), lines);
        Assert.Contains(line => line.Contains(Environment.ProcessPath!, StringComparison.Ordinal), lines);
    }

    [TestMethod]
    public void Run_NotElevated_TooFastOk_ShowsAgainAndElevatesOnlyAfterADeliberateAnswer()
    {
        var events = new List<string>();
        var messages = new List<string>();
        var scanner = Scanner(
            [],
            events,
            [Answer(SampleHost.MessageBoxResultOk, 0.2), Answer(SampleHost.MessageBoxResultOk, 3)],
            messages);

        var result = RunElevation(scanner);

        Assert.AreEqual(0, result);
        Assert.AreSequenceEqual(["dialog", "dialog", "elevate"], events);
        Assert.IsFalse(messages[0].Contains("too fast", StringComparison.Ordinal));
        Assert.IsTrue(messages[1].Contains("too fast", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Run_NotElevated_TooFastCancel_ShowsAgainRatherThanCancelling()
    {
        var events = new List<string>();
        var scanner = Scanner(
            [],
            events,
            [Answer(SampleHost.MessageBoxResultCancel, 0.2), Answer(SampleHost.MessageBoxResultOk, 3)]);

        var result = RunElevation(scanner);

        Assert.AreEqual(0, result);
        Assert.AreSequenceEqual(["dialog", "dialog", "elevate"], events);
    }

    [TestMethod]
    public void Run_NotElevated_DismissalExactlyAtTheGuardInterval_IsDeliberate()
    {
        var events = new List<string>();
        var scanner = Scanner(
            [], events, [Answer(SampleHost.MessageBoxResultOk, SampleHost.AccidentalDismissalInterval.TotalSeconds)]);

        RunElevation(scanner);

        Assert.AreSequenceEqual(["dialog", "elevate"], events);
    }

    [TestMethod]
    public void Run_NotElevated_DialogFailure_DoesNotElevateOrLoop()
    {
        var lines = new List<string>();
        var events = new List<string>();
        var scanner = Scanner(lines, events, [Answer(0, 0)]);

        var result = RunElevation(scanner);

        Assert.AreEqual(1, result);
        Assert.AreSequenceEqual(["dialog"], events);
        Assert.Contains(line => line.Contains("AUTOMATIC ELEVATION FAILED"), lines);
    }

    [TestMethod]
    public void Run_Elevated_ShowsNoDialog()
    {
        var events = new List<string>();
        var scanner = Scanner([], events, []);
        scanner._isElevated = () => true;

        RunElevation(scanner, ["scan", "T"]);

        Assert.AreSequenceEqual([], events);
    }

    [TestMethod]
    public void Run_NotElevated_CannotSelfElevate_ShowsNoDialog()
    {
        var events = new List<string>();
        var scanner = Scanner([], events, []);
        scanner._canSelfElevate = () => false;

        var result = RunElevation(scanner);

        Assert.AreEqual(1, result);
        Assert.AreSequenceEqual([], events);
    }

    [TestMethod]
    public void Run_NotElevated_DialogCarriesTheContractedTextAndFlags()
    {
        string? text = null;
        string? title = null;
        uint? type = null;
        uint? beep = null;
        var clock = new FakeTimeProvider();
        var scanner = new SampleHost(TextWriter.Null, TextReader.Null, _directory, clock)
        {
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _tryRunElevated = (_, _) => true,
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

        RunElevation(scanner, ["search", "C", "--name", "", "--under", "a b&c"]);

        Assert.AreEqual(SampleHost.MessageBeepIconExclamation, beep);
        Assert.IsTrue(title!.Contains("SampleProgram", StringComparison.Ordinal));
        const uint expectedFlags = SampleHost.MessageBoxOkCancel | SampleHost.MessageBoxIconInformation
            | SampleHost.MessageBoxSystemModal | SampleHost.MessageBoxSetForeground | SampleHost.MessageBoxTopMost;
        Assert.AreEqual(expectedFlags, type);
        Assert.IsTrue(text!.Contains("UAC", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("administrator rights", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains($"Executable: {Environment.ProcessPath}", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("Arguments (6)", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("  search", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("  <empty>", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("  a b&c", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Run_NotElevated_DialogNeverAnswered_TimesOutAsCancelWithoutElevating()
    {
        var lines = new List<string>();
        var events = new List<string>();
        var scanner = Scanner(lines, events, []);
        var clock = (FakeTimeProvider)scanner._timeProvider;
        var neverAnswered = new TaskCompletionSource();
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
            result = RunElevation(scanner);
        }
        finally
        {
            neverAnswered.SetResult();
        }

        Assert.AreEqual(1, result);
        Assert.AreSequenceEqual(["dialog"], events);
        Assert.Contains(line => line.Contains("not answered in time", StringComparison.Ordinal) || line.Contains("not answered in time"), lines);
        Assert.Contains(line => line.Contains("AUTOMATIC ELEVATION FAILED", StringComparison.Ordinal) || line.Contains("AUTOMATIC ELEVATION FAILED"), lines);
    }

    [TestMethod]
    public void Run_NotElevated_Unattended_SkipsTheDialogAndElevationAndPrintsTheFallback()
    {
        Environment.SetEnvironmentVariable(SampleHost.UnattendedVariableName, "1");
        var lines = new List<string>();
        var events = new List<string>();
        var scanner = Scanner(lines, events, []);

        var result = RunElevation(scanner);

        Assert.AreEqual(1, result);
        Assert.AreSequenceEqual([], events);
        Assert.Contains(line => line.Contains("Running unattended (MFTLIB_SAMPLE_UNATTENDED=1): elevation skipped"), lines);
        Assert.Contains(line => line.Contains("AUTOMATIC ELEVATION FAILED"), lines);
        Assert.Contains("  scan", lines);
    }

    [TestMethod]
    public void Run_LocalScan_Unattended_NeverScans()
    {
        Environment.SetEnvironmentVariable(SampleHost.UnattendedVariableName, "1");
        var lines = new List<string>();
        var scanner = Scanner(lines, [], []);

        var result = scanner.RunWithElevation(["scan", "C"], ElevationNeed.SelfElevate, () =>
            throw new AssertFailedException("An unattended local scan must not run."));

        Assert.AreEqual(1, result);
        Assert.Contains(line => line.Contains("Running unattended"), lines);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("0")]
    [DataRow("true")]
    public void Run_NotElevated_SwitchNotSetToOne_ShowsTheDialog(string? value)
    {
        Environment.SetEnvironmentVariable(SampleHost.UnattendedVariableName, value);
        var events = new List<string>();
        var scanner = Scanner([], events, [Answer(SampleHost.MessageBoxResultOk, 2)]);

        RunElevation(scanner);

        Assert.AreSequenceEqual(["dialog", "elevate"], events);
    }

    [TestMethod]
    public void Run_Elevated_IgnoresTheUnattendedSwitch()
    {
        Environment.SetEnvironmentVariable(SampleHost.UnattendedVariableName, "1");
        var lines = new List<string>();
        var scanner = Scanner(lines, [], []);
        scanner._isElevated = () => true;

        var result = scanner.RunWithElevation(["scan", "T"], ElevationNeed.SelfElevate, () =>
            throw new IOException("the scan was attempted"));

        Assert.AreEqual(1, result, "The scan was attempted and failed; the switch changed nothing.");
        Assert.DoesNotContain(line => line.Contains("Running unattended"), lines);
    }

    [TestMethod]
    public void Run_LocalScan_UnattendedButAlreadyElevated_ScansAsUsual()
    {
        Environment.SetEnvironmentVariable(SampleHost.UnattendedVariableName, "1");
        var lines = new List<string>();
        var scanner = Scanner(lines, [], []);
        scanner._isElevated = () => true;

        var result = scanner.RunWithElevation(["scan", "C"], ElevationNeed.SelfElevate, () =>
            throw new IOException("the scan was attempted"));

        Assert.AreEqual(1, result);
        var report = File.ReadAllText(scanner.OutputLogPath);
        Assert.Contains("Error: IOException: the scan was attempted", report);
        Assert.DoesNotContain(line => line.Contains("Running unattended"), lines);
    }

    static (int Result, double SecondsToAnswer) Answer(int result, double secondsToAnswer) => (result, secondsToAnswer);

    SampleHost Scanner(
        List<string> lines,
        List<string> events,
        (int Result, double SecondsToAnswer)[] answers,
        List<string>? messages = null)
    {
        var clock = new FakeTimeProvider();
        var next = 0;
        // The scanner of a run that must itself be elevated.
        return new SampleHost(new LineCollectingWriter(lines), TextReader.Null, _directory, clock)
        {
            _isElevated = () => false,
            _canSelfElevate = () => true,
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
