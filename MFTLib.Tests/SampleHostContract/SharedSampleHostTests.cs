using MFTLib.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.SampleHostContract;

// The heads-up dialog before a sample's elevation request, as both samples compile it from SampleProgram.Shared. The dialog
// and the clock are scripted: each stubbed dialog answers after a chosen amount of fake time, so no test sleeps or reads
// the real clock. A concrete subclass binds the whole contract to one sample's copy of SampleHost.
[DoNotParallelize]
public abstract partial class SharedSampleHostTests
{
    string? _previousUnattended;
    string _unattendedVariableName = null!;

    internal abstract SampleHostSubject CreateSubject(TextWriter output, TextReader input, FakeTimeProvider clock, Action elevated);

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void SetUp()
    {
        _unattendedVariableName = CreateSubject(TextWriter.Null, TextReader.Null, new FakeTimeProvider(), () => { }).UnattendedVariableName;
        _previousUnattended = Environment.GetEnvironmentVariable(_unattendedVariableName);
        Environment.SetEnvironmentVariable(_unattendedVariableName, null);
    }

    [TestCleanup]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(_unattendedVariableName, _previousUnattended);
    }

    // Only the sample's constants are read from it.
    SampleHostSubject Probe => CreateSubject(TextWriter.Null, TextReader.Null, new FakeTimeProvider(), () => { });

    static (int Result, double SecondsToAnswer) Answer(int result, double secondsToAnswer) => (result, secondsToAnswer);

    SampleHostSubject Scanner(
        List<string> lines,
        List<string> events,
        (int Result, double SecondsToAnswer)[] answers,
        List<string>? messages = null)
    {
        var clock = new FakeTimeProvider();
        var next = 0;
        var scanner = CreateSubject(new LineCollectingWriter(lines), TextReader.Null, clock, () => events.Add("elevate"));
        scanner.SetMessageBox((_, text, _, _) =>
        {
            events.Add("dialog");
            messages?.Add(text);
            var answer = answers[next++];
            clock.Advance(TimeSpan.FromSeconds(answer.SecondsToAnswer));
            return answer.Result;
        });
        return scanner;
    }

    static int RunElevation(SampleHostSubject scanner, string[]? arguments = null) =>
        scanner.RunElevation(arguments ?? ["scan", "C"]);

    [TestMethod]
    public void Run_NotElevated_DeliberateOk_ElevatesExactlyOnceAfterTheDialog()
    {
        var events = new List<string>();
        var probe = Probe;
        var scanner = Scanner([], events, [Answer(probe.ResultOk, 2)]);

        var result = RunElevation(scanner);

        Assert.AreEqual(0, result);
        Assert.AreSequenceEqual(["dialog", "elevate"], events);
    }

    [TestMethod]
    public void Run_NotElevated_DeliberateCancel_SkipsElevationAndPrintsTheFallback()
    {
        var lines = new List<string>();
        var events = new List<string>();
        var scanner = Scanner(lines, events, [Answer(Probe.ResultCancel, 2)]);

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
        var ok = Probe.ResultOk;
        var scanner = Scanner([], events, [Answer(ok, 0.2), Answer(ok, 3)], messages);

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
        var probe = Probe;
        var scanner = Scanner([], events, [Answer(probe.ResultCancel, 0.2), Answer(probe.ResultOk, 3)]);

        var result = RunElevation(scanner);

        Assert.AreEqual(0, result);
        Assert.AreSequenceEqual(["dialog", "dialog", "elevate"], events);
    }

    [TestMethod]
    public void Run_NotElevated_DismissalExactlyAtTheGuardInterval_IsDeliberate()
    {
        var events = new List<string>();
        var probe = Probe;
        var scanner = Scanner([], events, [Answer(probe.ResultOk, probe.AccidentalInterval.TotalSeconds)]);

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
        Assert.Contains(line => line.Contains("AUTOMATIC ELEVATION FAILED", StringComparison.Ordinal), lines);
    }

    [TestMethod]
    public void Run_Elevated_ShowsNoDialog()
    {
        var events = new List<string>();
        var scanner = Scanner([], events, []);
        scanner.SetElevated(true);

        RunElevation(scanner, ["scan", "T"]);

        Assert.DoesNotContain("dialog", events);
    }

    [TestMethod]
    public void Run_NotElevated_DialogCarriesTheContractedTextAndFlags()
    {
        string? text = null;
        string? title = null;
        uint? type = null;
        uint? beep = null;
        var scanner = Scanner([], [], []);
        scanner.SetMessageBeep(sound =>
        {
            beep = sound;
            return true;
        });
        scanner.SetMessageBox((_, body, caption, flags) =>
        {
            text = body;
            title = caption;
            type = flags;
            scanner.Clock.Advance(TimeSpan.FromSeconds(5));
            return scanner.ResultOk;
        });

        RunElevation(scanner, ["search", "C", "--name", "", "--under", "a b&c"]);

        Assert.AreEqual(scanner.BeepIcon, beep);
        Assert.AreEqual(scanner.Title, title);
        Assert.IsTrue(title!.Contains("SampleProgram", StringComparison.Ordinal));
        Assert.AreEqual(scanner.ExpectedFlags, type);
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
        var neverAnswered = new TaskCompletionSource();
        scanner.SetMessageBox((_, _, _, _) =>
        {
            events.Add("dialog");
            // The abandoned dialog is still open when the wait runs out: advance the fake clock past it and hang.
            scanner.Clock.Advance(scanner.NoticeTimeout);
            // Runs on the dialog's own pool thread, which exists only to block here until the test releases it.
            neverAnswered.Task.Wait(TestContext.CancellationToken);
            return scanner.ResultOk;
        });

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
        Assert.Contains(line => line.Contains("not answered in time", StringComparison.Ordinal), lines);
        Assert.Contains(line => line.Contains("AUTOMATIC ELEVATION FAILED", StringComparison.Ordinal), lines);
    }

    [TestMethod]
    public void Run_NotElevated_Unattended_SkipsTheDialogAndElevationAndPrintsTheFallback()
    {
        Environment.SetEnvironmentVariable(_unattendedVariableName, "1");
        var lines = new List<string>();
        var events = new List<string>();
        var scanner = Scanner(lines, events, []);

        var result = RunElevation(scanner);

        Assert.AreEqual(1, result);
        Assert.AreSequenceEqual([], events);
        Assert.Contains(line => line.Contains($"Running unattended ({_unattendedVariableName}=1): elevation skipped", StringComparison.Ordinal), lines);
        Assert.Contains(line => line.Contains("AUTOMATIC ELEVATION FAILED", StringComparison.Ordinal), lines);
        Assert.Contains("  scan", lines);
    }

    [TestMethod]
    public void Run_Gated_Unattended_NeverRunsTheContinuation()
    {
        Environment.SetEnvironmentVariable(_unattendedVariableName, "1");
        var lines = new List<string>();
        var scanner = Scanner(lines, [], []);

        var result = scanner.RunGated(["scan", "C"], () =>
            throw new AssertFailedException("An unattended run must not continue past the elevation gate."));

        Assert.AreEqual(1, result);
        Assert.Contains(line => line.Contains("Running unattended", StringComparison.Ordinal), lines);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("0")]
    [DataRow("true")]
    public void Run_NotElevated_SwitchNotSetToOne_ShowsTheDialog(string? value)
    {
        Environment.SetEnvironmentVariable(_unattendedVariableName, value);
        var events = new List<string>();
        var scanner = Scanner([], events, [Answer(Probe.ResultOk, 2)]);

        RunElevation(scanner);

        Assert.AreSequenceEqual(["dialog", "elevate"], events);
    }
}
