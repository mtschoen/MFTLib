using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The Windows Defender exclusion sequence: the three-branch dispatch, the timeout rule, the script, the
///     input validation and the one PowerShell runner. Every launch is scripted, so no test raises a UAC prompt,
///     starts a real PowerShell or touches real Defender settings.
/// </summary>
[TestClass]
[DoNotParallelize]
public class DefenderExclusionSequenceTests
{
    static readonly string[] OneName = ["app.exe"];
    static readonly string[] Relaunch = ["--elevated-defender"];

    sealed class FakeElevation(bool elevated, bool canSelfElevate, bool relaunchResult = true) : IElevationProvider
    {
        public List<(IReadOnlyList<string> Arguments, TimeSpan Timeout)> Relaunches { get; } = [];

        public bool IsElevated() => elevated;

        public bool CanSelfElevate() => canSelfElevate;

        public bool TryRunElevated(IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            Relaunches.Add((arguments, timeout));
            return relaunchResult;
        }
    }

    sealed class RecordingRunner(bool result = true)
    {
        public List<(string Script, bool Elevated)> Launches { get; } = [];

        public bool Run(string script, bool elevated)
        {
            Launches.Add((script, elevated));
            return result;
        }
    }

    // --- dispatch ---

    [TestMethod]
    public void Run_AlreadyElevated_RunsThePowerShellDirectlyWithoutTheElevationVerb()
    {
        var elevation = new FakeElevation(elevated: true, canSelfElevate: true);
        var runner = new RecordingRunner();

        var result = DefenderExclusionSequence.Run(OneName, Relaunch, elevation, true, runner.Run);

        Assert.IsTrue(result);
        Assert.AreEqual(0, elevation.Relaunches.Count);
        Assert.AreEqual(("Add-MpPreference -ExclusionProcess 'app.exe' -ErrorAction Stop", false), runner.Launches.Single());
    }

    [TestMethod]
    public void Run_AlreadyElevatedAndThePowerShellFails_ReturnsFalse()
    {
        var runner = new RecordingRunner(result: false);

        Assert.IsFalse(DefenderExclusionSequence.Run(OneName, Relaunch, new FakeElevation(true, false), true, runner.Run));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Run_CanSelfElevate_RelaunchesWithTheArgumentsAndReturnsItsResultWithoutPowerShell(bool relaunchResult)
    {
        var elevation = new FakeElevation(elevated: false, canSelfElevate: true, relaunchResult);
        var runner = new RecordingRunner();

        var result = DefenderExclusionSequence.Run(OneName, Relaunch, elevation, true, runner.Run);

        Assert.AreEqual(relaunchResult, result);
        Assert.AreEqual(0, runner.Launches.Count);
        var (arguments, timeout) = elevation.Relaunches.Single();
        CollectionAssert.AreEqual(Relaunch, arguments.ToArray());
        Assert.AreEqual(DefenderExclusionSequence.ElevatedTimeout, timeout);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Run_CannotSelfElevate_UsesTheElevatedPowerShellAndReturnsItsResult(bool powerShellResult)
    {
        var elevation = new FakeElevation(elevated: false, canSelfElevate: false);
        var runner = new RecordingRunner(powerShellResult);

        var result = DefenderExclusionSequence.Run(OneName, Relaunch, elevation, true, runner.Run);

        Assert.AreEqual(powerShellResult, result);
        Assert.AreEqual(0, elevation.Relaunches.Count);
        Assert.IsTrue(runner.Launches.Single().Elevated);
    }

    [TestMethod]
    public void Run_NotWindows_ReturnsFalseWithoutAskingElevationOrLaunchingAnything()
    {
        var elevation = new FakeElevation(true, true);
        var runner = new RecordingRunner();

        Assert.IsFalse(DefenderExclusionSequence.Run(OneName, Relaunch, elevation, false, runner.Run));
        Assert.AreEqual(0, elevation.Relaunches.Count);
        Assert.AreEqual(0, runner.Launches.Count);
    }

    [TestMethod]
    public void ElevatedTimeout_ExceedsThePowerShellTimeoutWithSlackToStartAndExit()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(30), DefenderExclusionSequence.PowerShellTimeout);
        Assert.IsTrue(DefenderExclusionSequence.ElevatedTimeout > DefenderExclusionSequence.PowerShellTimeout);
        Assert.AreEqual(
            DefenderExclusionSequence.PowerShellTimeout + DefenderExclusionSequence.ElevatedChildSlack,
            DefenderExclusionSequence.ElevatedTimeout);
        Assert.AreEqual(ElevationUtilities.DefaultElevatedTimeout, DefenderExclusionSequence.ElevatedTimeout);
    }

    // --- script text ---

    [TestMethod]
    public void BuildScript_OneName_IsOneAddMpPreferenceCommand()
    {
        Assert.AreEqual(
            "Add-MpPreference -ExclusionProcess 'git.exe' -ErrorAction Stop",
            DefenderExclusionSequence.BuildScript(["git.exe"]));
    }

    [TestMethod]
    public void BuildScript_SeveralNames_JoinsOneCommandPerNameInOrder()
    {
        Assert.AreEqual(
            "Add-MpPreference -ExclusionProcess 'git.exe' -ErrorAction Stop; "
            + "Add-MpPreference -ExclusionProcess 'My App-1_2+x.exe' -ErrorAction Stop",
            DefenderExclusionSequence.BuildScript(["git.exe", "My App-1_2+x.exe"]));
    }

    // --- input safety ---

    [DataTestMethod]
    [DataRow("", DisplayName = "empty")]
    [DataRow("   ", DisplayName = "whitespace-only")]
    [DataRow(" app.exe", DisplayName = "leading-space")]
    [DataRow("app.exe ", DisplayName = "trailing-space")]
    [DataRow("..", DisplayName = "dots-only")]
    [DataRow("a'b.exe", DisplayName = "single-quote")]
    [DataRow("a\"b.exe", DisplayName = "double-quote")]
    [DataRow("a;b.exe", DisplayName = "semicolon")]
    [DataRow("a`b.exe", DisplayName = "backtick")]
    [DataRow("a$b.exe", DisplayName = "dollar")]
    [DataRow("a|b.exe", DisplayName = "pipe")]
    [DataRow("a&b.exe", DisplayName = "ampersand")]
    [DataRow("a(b).exe", DisplayName = "parentheses")]
    [DataRow("a\\b.exe", DisplayName = "backslash")]
    [DataRow("C:\\a.exe", DisplayName = "drive-path")]
    [DataRow("a/b.exe", DisplayName = "slash")]
    [DataRow("a\nb.exe", DisplayName = "newline")]
    [DataRow("a\tb.exe", DisplayName = "tab")]
    public void BuildScript_NameThatIsNotAPlainFileName_IsRefused(string name)
    {
        Assert.ThrowsException<ArgumentException>(() => DefenderExclusionSequence.BuildScript(["ok.exe", name]));
    }

    [TestMethod]
    public void BuildScript_NullName_IsRefused()
    {
        Assert.ThrowsException<ArgumentException>(() => DefenderExclusionSequence.BuildScript([null!]));
    }

    [TestMethod]
    public void BuildScript_InvalidName_ReportsThePublicParameterName()
    {
        var exception = Assert.ThrowsException<ArgumentException>(() => DefenderExclusionSequence.BuildScript(["a;b.exe"]));

        Assert.AreEqual("processNames", exception.ParamName);
    }

    [TestMethod]
    public void BuildScript_EmptyList_IsRefused()
    {
        Assert.ThrowsException<ArgumentException>(() => DefenderExclusionSequence.BuildScript([]));
    }

    [TestMethod]
    public void BuildScript_NullList_IsRefused()
    {
        Assert.ThrowsException<ArgumentNullException>(() => DefenderExclusionSequence.BuildScript(null!));
    }

    [TestMethod]
    public void Run_NullRelaunchArguments_IsRefused()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => DefenderExclusionSequence.Run(OneName, null!, new FakeElevation(true, true), true, new RecordingRunner().Run));
    }

    [TestMethod]
    public void Run_InvalidNameOnANonWindowsPlatform_StillThrowsBeforeAnythingElse()
    {
        var elevation = new FakeElevation(true, true);
        var runner = new RecordingRunner();

        Assert.ThrowsException<ArgumentException>(
            () => DefenderExclusionSequence.Run(["a;b.exe"], Relaunch, elevation, false, runner.Run));
        Assert.AreEqual(0, runner.Launches.Count);
    }

    // --- the one PowerShell runner ---

    static readonly string TrustedPowerShellPath =
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [TestMethod]
    public void PowerShellPath_IsTheSystemDirectoryCopyNotASearchedName()
    {
        Assert.AreEqual(TrustedPowerShellPath, DefenderExclusionSequence.PowerShellPath);
        Assert.AreNotEqual("powershell.exe", DefenderExclusionSequence.PowerShellPath);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsTrue(Path.IsPathFullyQualified(DefenderExclusionSequence.PowerShellPath), DefenderExclusionSequence.PowerShellPath);
        }
    }

    static DefenderExclusionSequence SequenceOver(
        Func<ProcessStartInfo, Process?>? startProcess = null,
        Func<Process, TimeSpan, bool>? waitForExit = null,
        Action<Process>? killProcess = null,
        int exitCode = 0)
    {
        return new DefenderExclusionSequence(new ElevationSeams
        {
            StartProcess = startProcess ?? (_ => new Process()),
            WaitForExit = waitForExit ?? ((_, _) => true),
            KillProcess = killProcess ?? (_ => { }),
            GetExitCode = _ => exitCode
        });
    }

    [TestMethod]
    public void RunPowerShell_Elevated_UsesTheRunAsVerbAndPassesTheScriptEncoded()
    {
        ProcessStartInfo? started = null;
        var sequence = SequenceOver(startInfo =>
        {
            started = startInfo;
            return new Process();
        });
        const string script = "Add-MpPreference -ExclusionProcess 'app.exe' -ErrorAction Stop";

        Assert.IsTrue(sequence.RunPowerShell(script, elevated: true));

        Assert.IsNotNull(started);
        Assert.AreEqual(TrustedPowerShellPath, started.FileName);
        Assert.AreEqual("runas", started.Verb);
        Assert.IsTrue(started.UseShellExecute);
        Assert.AreEqual(string.Empty, started.Arguments);
        CollectionAssert.AreEqual(
            new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand" },
            started.ArgumentList.Take(3).ToArray());
        Assert.AreEqual(4, started.ArgumentList.Count);
        Assert.AreEqual(script, Encoding.Unicode.GetString(Convert.FromBase64String(started.ArgumentList[3])));
    }

    [TestMethod]
    public void RunPowerShell_NotElevated_StartsWithoutShellExecuteOrVerb()
    {
        ProcessStartInfo? started = null;
        var sequence = SequenceOver(startInfo =>
        {
            started = startInfo;
            return new Process();
        });

        Assert.IsTrue(sequence.RunPowerShell("script", elevated: false));

        Assert.IsNotNull(started);
        Assert.AreEqual(string.Empty, started.Verb);
        Assert.IsFalse(started.UseShellExecute);
        Assert.AreEqual(TrustedPowerShellPath, started.FileName, "The direct, already elevated branch must not search PATH either.");
    }

    [DataTestMethod]
    [DataRow(0, true)]
    [DataRow(1, false)]
    public void RunPowerShell_Exit_IsAppliedOnlyForZeroAndNeverKills(int exitCode, bool expected)
    {
        var killed = new List<Process>();
        var sequence = SequenceOver(exitCode: exitCode, killProcess: killed.Add);

        Assert.AreEqual(expected, sequence.RunPowerShell("script", false));
        Assert.AreEqual(0, killed.Count);
    }

    [TestMethod]
    public void DefaultProcessOperations_OnAProcessThatNeverStarted_ThrowInvalidOperation()
    {
        ThrowsInvalidOperation(process => DefenderExclusionSequence.WaitForExit(process, TimeSpan.Zero));
        ThrowsInvalidOperation(DefenderExclusionSequence.Kill);
        ThrowsInvalidOperation(process => DefenderExclusionSequence.ExitCode(process));
    }

    static void ThrowsInvalidOperation(Action<Process> operation)
    {
        using var neverStarted = new Process();
        try
        {
            operation(neverStarted);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        Assert.Fail("A process that never started has nothing to wait for, kill or read an exit code from.");
    }

    [TestMethod]
    public void RunPowerShell_ProcessDoesNotStart_ReturnsFalse()
    {
        Assert.IsFalse(SequenceOver(startProcess: _ => null).RunPowerShell("script", true));
    }

    [TestMethod]
    public void RunPowerShell_WaitTimesOut_WaitsThePowerShellTimeoutKillsAndReturnsFalse()
    {
        var waited = new List<TimeSpan>();
        var killed = new List<Process>();
        var sequence = SequenceOver(
            waitForExit: (_, timeout) =>
            {
                waited.Add(timeout);
                return false;
            },
            killProcess: killed.Add);

        Assert.IsFalse(sequence.RunPowerShell("script", true));
        CollectionAssert.AreEqual(new[] { DefenderExclusionSequence.PowerShellTimeout }, waited);
        Assert.AreEqual(1, killed.Count);
    }

    [TestMethod]
    public void RunPowerShell_KillFails_ReturnsFalse()
    {
        var sequence = SequenceOver(
            waitForExit: (_, _) => false,
            killProcess: _ => throw new InvalidOperationException("already exited"));

        Assert.IsFalse(sequence.RunPowerShell("script", true));
    }

    [TestMethod]
    public void RunPowerShell_UacDeclined_ReturnsFalse()
    {
        var sequence = SequenceOver(startProcess: _ => throw new Win32Exception(1223));

        Assert.IsFalse(sequence.RunPowerShell("script", true));
    }

    [TestMethod]
    public void RunPowerShell_ExceptionThatIsNotAStartFailure_Propagates()
    {
        var sequence = SequenceOver(startProcess: _ => throw new NotSupportedException("unexpected"));

        Assert.ThrowsException<NotSupportedException>(() => sequence.RunPowerShell("script", true));
    }

    // --- the public member and the test-extension scope ---

    [TestMethod]
    public void TryAddDefenderProcessExclusions_NullElevation_UsesTheDefaultProviderOnlyAfterValidation()
    {
        Assert.ThrowsException<ArgumentException>(
            () => ElevationUtilities.TryAddDefenderProcessExclusions(["a;b.exe"], Relaunch));
    }

    [TestMethod]
    public void TryAddDefenderProcessExclusions_WithoutAScope_RefusesAnInvalidNameBeforeAnyLaunch()
    {
        Assert.ThrowsException<ArgumentException>(
            () => ElevationUtilities.TryAddDefenderProcessExclusions([""], Relaunch, new FakeElevation(true, true)));
    }

    [TestMethod]
    public void TryAddDefenderProcessExclusions_UnderAScope_RecordsTheLaunchOnWindowsAndReportsNotAppliedElsewhere()
    {
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        var applied = ElevationUtilities.TryAddDefenderProcessExclusions(
            OneName, Relaunch, new FakeElevation(elevated: true, canSelfElevate: false));

        Assert.AreEqual(OperatingSystem.IsWindows(), applied);
        Assert.AreEqual(OperatingSystem.IsWindows() ? 1 : 0, scripted.Launches.Count);
    }

    [TestMethod]
    public void ScriptPowerShell_RecordsEachLaunchAndReportsTheScriptedOutcome()
    {
        var runner = DefenderExclusionSequence.RunnerOverride;
        Assert.IsNull(runner);

        using (var failing = DefenderExclusionIsolation.ScriptPowerShell(succeeds: false))
        {
            var result = DefenderExclusionSequence.Run(
                OneName, Relaunch, new FakeElevation(false, false), true, DefenderExclusionSequence.RunnerOverride!);

            Assert.IsFalse(result);
            var launch = failing.Launches.Single();
            Assert.AreEqual("Add-MpPreference -ExclusionProcess 'app.exe' -ErrorAction Stop", launch.Script);
            Assert.IsTrue(launch.IsElevated);
        }

        using var succeeding = DefenderExclusionIsolation.ScriptPowerShell();
        Assert.IsTrue(DefenderExclusionSequence.RunnerOverride!("script", false));
        Assert.AreEqual("script", succeeding.Launches.Single().Script);
        Assert.IsFalse(succeeding.Launches.Single().IsElevated);
    }

    [TestMethod]
    public void ScriptPowerShell_NestedScope_IsRefusedUntilTheFirstIsDisposed()
    {
        var first = DefenderExclusionIsolation.ScriptPowerShell();
        try
        {
            Assert.ThrowsException<InvalidOperationException>(() => DefenderExclusionIsolation.ScriptPowerShell());
        }
        finally
        {
            first.Dispose();
        }

        first.Dispose();
        Assert.IsNull(DefenderExclusionSequence.RunnerOverride);
        DefenderExclusionIsolation.ScriptPowerShell().Dispose();
    }
}
