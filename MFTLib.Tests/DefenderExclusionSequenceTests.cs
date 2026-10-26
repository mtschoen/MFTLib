using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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
public partial class DefenderExclusionSequenceTests
{
    static readonly string[] OneName = ["app.exe"];

    const string ImportPrefix =
        @"Import-Module -Name (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules\Defender\Defender.psd1') -ErrorAction Stop; ";
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
        Assert.IsEmpty(elevation.Relaunches);
        Assert.AreEqual((ImportPrefix + "Defender\\Add-MpPreference -ExclusionProcess 'app.exe' -ErrorAction Stop", false), runner.Launches.Single());
    }

    [TestMethod]
    public void Run_AlreadyElevatedAndThePowerShellFails_ReturnsFalse()
    {
        var runner = new RecordingRunner(result: false);

        Assert.IsFalse(DefenderExclusionSequence.Run(OneName, Relaunch, new FakeElevation(true, false), true, runner.Run));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Run_CanSelfElevate_RelaunchesWithTheArgumentsAndReturnsItsResultWithoutPowerShell(bool relaunchResult)
    {
        var elevation = new FakeElevation(elevated: false, canSelfElevate: true, relaunchResult);
        var runner = new RecordingRunner();

        var result = DefenderExclusionSequence.Run(OneName, Relaunch, elevation, true, runner.Run);

        Assert.AreEqual(relaunchResult, result);
        Assert.IsEmpty(runner.Launches);
        var (arguments, timeout) = elevation.Relaunches.Single();
        Assert.AreSequenceEqual(Relaunch, [.. arguments]);
        Assert.AreEqual(DefenderExclusionSequence.ElevatedTimeout, timeout);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Run_CannotSelfElevate_UsesTheElevatedPowerShellAndReturnsItsResult(bool powerShellResult)
    {
        var elevation = new FakeElevation(elevated: false, canSelfElevate: false);
        var runner = new RecordingRunner(powerShellResult);

        var result = DefenderExclusionSequence.Run(OneName, Relaunch, elevation, true, runner.Run);

        Assert.AreEqual(powerShellResult, result);
        Assert.IsEmpty(elevation.Relaunches);
        Assert.IsTrue(runner.Launches.Single().Elevated);
    }

    [TestMethod]
    public void Run_NotWindows_ReturnsFalseWithoutAskingElevationOrLaunchingAnything()
    {
        var elevation = new FakeElevation(true, true);
        var runner = new RecordingRunner();

        Assert.IsFalse(DefenderExclusionSequence.Run(OneName, Relaunch, elevation, false, runner.Run));
        Assert.IsEmpty(elevation.Relaunches);
        Assert.IsEmpty(runner.Launches);
    }

    [TestMethod]
    public void ElevatedTimeout_ExceedsThePowerShellTimeoutWithSlackToStartAndExit()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(30), DefenderExclusionSequence.PowerShellTimeout);
        Assert.IsGreaterThan(DefenderExclusionSequence.PowerShellTimeout, DefenderExclusionSequence.ElevatedTimeout);
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
            ImportPrefix + "Defender\\Add-MpPreference -ExclusionProcess 'git.exe' -ErrorAction Stop",
            DefenderExclusionSequence.BuildScript(["git.exe"]));
    }

    [TestMethod]
    public void BuildScript_ImportsTheSystemModuleByAbsolutePathAndNeverCallsABareMpCmdlet()
    {
        var script = DefenderExclusionSequence.BuildScript(["git.exe", "app.exe"]);

        Assert.StartsWith("Import-Module -Name (Join-Path $env:SystemRoot ", script);
        Assert.Contains(@"System32\WindowsPowerShell\v1.0\Modules\Defender\Defender.psd1", script);
        Assert.Contains("-ErrorAction Stop", script);
        Assert.AreEqual(2, QualifiedDefenderCommandRegex().Count(script));
        Assert.IsEmpty(UnqualifiedDefenderCommandRegex().Matches(script));
    }

    [TestMethod]
    public void BuildScript_SeveralNames_JoinsOneCommandPerNameInOrder()
    {
        Assert.AreEqual(
            ImportPrefix + "Defender\\Add-MpPreference -ExclusionProcess 'git.exe' -ErrorAction Stop; "
            + "Defender\\Add-MpPreference -ExclusionProcess 'My App-1_2+x.exe' -ErrorAction Stop",
            DefenderExclusionSequence.BuildScript(["git.exe", "My App-1_2+x.exe"]));
    }

    // --- input safety ---

    [TestMethod]
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
        Assert.ThrowsExactly<ArgumentException>(() => DefenderExclusionSequence.BuildScript(["ok.exe", name]));
    }

    [TestMethod]
    public void BuildScript_NullName_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => DefenderExclusionSequence.BuildScript([null!]));
    }

    [TestMethod]
    public void BuildScript_InvalidName_ReportsThePublicParameterName()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() => DefenderExclusionSequence.BuildScript(["a;b.exe"]));

        Assert.AreEqual("processNames", exception.ParamName);
    }

    [TestMethod]
    public void BuildScript_EmptyList_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => DefenderExclusionSequence.BuildScript([]));
    }

    [TestMethod]
    public void BuildScript_NullList_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => DefenderExclusionSequence.BuildScript(null!));
    }

    [TestMethod]
    public void Run_NullRelaunchArguments_IsRefused()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => DefenderExclusionSequence.Run(OneName, null!, new FakeElevation(true, true), true, new RecordingRunner().Run));
    }

    [TestMethod]
    public void Run_InvalidNameOnANonWindowsPlatform_StillThrowsBeforeAnythingElse()
    {
        var elevation = new FakeElevation(true, true);
        var runner = new RecordingRunner();

        Assert.ThrowsExactly<ArgumentException>(
            () => DefenderExclusionSequence.Run(["a;b.exe"], Relaunch, elevation, false, runner.Run));
        Assert.IsEmpty(runner.Launches);
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
        int exitCode = 0,
        string? temporaryDirectory = null)
    {
        return new DefenderExclusionSequence(new ElevationSeams
        {
            StartProcess = startProcess ?? (_ => new Process()),
            WaitForExit = waitForExit ?? ((_, _) => true),
            KillProcess = killProcess ?? (_ => { }),
            GetExitCode = _ => exitCode,
            GetTemporaryDirectory = temporaryDirectory is null ? null : () => temporaryDirectory
        });
    }

    static string ScriptPathOf(ProcessStartInfo started) => started.ArgumentList[^1];

    static string CreateOwnedDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MFTLib.DefenderExclusionSequenceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [TestMethod]
    public void RunPowerShell_Elevated_UsesTheRunAsVerbAndRunsTheScriptFromALockedFile()
    {
        var directory = CreateOwnedDirectory();
        try
        {
            ProcessStartInfo? started = null;
            byte[]? contentWhileRunning = null;
            var lockedAgainstWrites = false;
            var lockedAgainstDeletes = false;
            var sequence = SequenceOver(
                startInfo =>
                {
                    started = startInfo;
                    var path = ScriptPathOf(startInfo);
                    contentWhileRunning = ReadSharedRead(path);
                    lockedAgainstWrites = OpeningThrows(path, FileAccess.Write, FileShare.ReadWrite);
                    lockedAgainstDeletes = MovingThrows(path);
                    return new Process();
                },
                temporaryDirectory: directory);
            const string script = "Add-MpPreference -ExclusionProcess 'app.exe' -ErrorAction Stop; # caf\u00e9";

            Assert.IsTrue(sequence.RunPowerShell(script, elevated: true));

            Assert.IsNotNull(started);
            Assert.AreEqual(TrustedPowerShellPath, started.FileName);
            Assert.AreEqual("runas", started.Verb);
            Assert.IsTrue(started.UseShellExecute);
            Assert.AreEqual(string.Empty, started.Arguments);
            Assert.AreSequenceEqual(
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"],
                [.. started.ArgumentList.Take(5)]);
            Assert.HasCount(6, started.ArgumentList);
            var path = ScriptPathOf(started);
            Assert.AreEqual(directory, Path.GetDirectoryName(path));
            Assert.AreEqual(".ps1", Path.GetExtension(path));
            Assert.IsNotNull(contentWhileRunning);
            Assert.AreSequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }, [.. contentWhileRunning.Take(3)]);
            Assert.AreEqual(script, new UTF8Encoding(false).GetString(contentWhileRunning, 3, contentWhileRunning.Length - 3));
            if (OperatingSystem.IsWindows())
            {
                Assert.IsTrue(lockedAgainstWrites, "The script file must refuse a writer while the child runs.");
                Assert.IsTrue(lockedAgainstDeletes, "The script file must refuse a delete or rename while the child runs.");
            }

            Assert.IsFalse(File.Exists(path), "The script file is deleted afterward.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    static byte[] ReadSharedRead(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    static bool MovingThrows(string path)
    {
        try
        {
            File.Move(path, path + ".moved");
            File.Move(path + ".moved", path);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    static bool OpeningThrows(string path, FileAccess access, FileShare share)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, access, share);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    [TestMethod]
    public void RunPowerShell_EachRun_UsesItsOwnScriptFileAndDeletesIt()
    {
        var directory = CreateOwnedDirectory();
        try
        {
            var paths = new List<string>();
            var sequence = SequenceOver(
                startInfo =>
                {
                    paths.Add(ScriptPathOf(startInfo));
                    return new Process();
                },
                temporaryDirectory: directory);

            Assert.IsTrue(sequence.RunPowerShell("first", false));
            Assert.IsTrue(sequence.RunPowerShell("second", false));

            Assert.HasCount(2, paths);
            Assert.AreNotEqual(paths[0], paths[1]);
            Assert.IsEmpty(Directory.GetFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(0)]
    public void RunPowerShell_ProcessOutcome_StillDeletesTheScriptFile(int exitCode)
    {
        var directory = CreateOwnedDirectory();
        try
        {
            SequenceOver(exitCode: exitCode, temporaryDirectory: directory).RunPowerShell("script", true);

            Assert.IsEmpty(Directory.GetFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void RunPowerShell_FailurePaths_DeleteTheScriptFile()
    {
        var directory = CreateOwnedDirectory();
        try
        {
            Assert.IsFalse(SequenceOver(startProcess: _ => null, temporaryDirectory: directory).RunPowerShell("script", true));
            Assert.IsFalse(SequenceOver(
                waitForExit: (_, _) => false, temporaryDirectory: directory).RunPowerShell("script", true));
            Assert.IsFalse(SequenceOver(
                startProcess: _ => throw new Win32Exception(1223), temporaryDirectory: directory).RunPowerShell("script", true));
            Assert.ThrowsExactly<NotSupportedException>(() => SequenceOver(
                startProcess: _ => throw new NotSupportedException("unexpected"),
                temporaryDirectory: directory).RunPowerShell("script", true));

            Assert.IsEmpty(Directory.GetFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void RunPowerShell_ScriptFileCannotBeCreated_ReturnsFalseWithoutStarting()
    {
        var directory = CreateOwnedDirectory();
        try
        {
            var blocker = Path.Combine(directory, "not-a-directory");
            File.WriteAllText(blocker, "a file where the directory should be");
            var started = false;

            var applied = SequenceOver(
                startProcess: _ =>
                {
                    started = true;
                    return new Process();
                },
                temporaryDirectory: blocker).RunPowerShell("script", true);

            Assert.IsFalse(applied);
            Assert.IsFalse(started);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void RunPowerShell_ScriptFileStillOpenElsewhere_DoesNotMaskTheResult()
    {
        var directory = CreateOwnedDirectory();
        try
        {
            FileStream? outsider = null;
            var sequence = SequenceOver(
                startInfo =>
                {
                    outsider = new FileStream(ScriptPathOf(startInfo), FileMode.Open, FileAccess.Read, FileShare.Read);
                    return new Process();
                },
                temporaryDirectory: directory);

            try
            {
                Assert.IsTrue(sequence.RunPowerShell("script", false));
            }
            finally
            {
                outsider?.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

    [TestMethod]
    [DataRow(0, true)]
    [DataRow(1, false)]
    public void RunPowerShell_Exit_IsAppliedOnlyForZeroAndNeverKills(int exitCode, bool expected)
    {
        var killed = new List<Process>();
        var sequence = SequenceOver(killProcess: killed.Add, exitCode: exitCode);

        Assert.AreEqual(expected, sequence.RunPowerShell("script", false));
        Assert.IsEmpty(killed);
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
        Assert.AreSequenceEqual([DefenderExclusionSequence.PowerShellTimeout], waited);
        Assert.HasCount(1, killed);
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

        Assert.ThrowsExactly<NotSupportedException>(() => sequence.RunPowerShell("script", true));
    }

    // --- the public member and the test-extension scope ---

    [TestMethod]
    public void TryAddDefenderProcessExclusions_NullElevation_UsesTheDefaultProviderOnlyAfterValidation()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => ElevationUtilities.TryAddDefenderProcessExclusions(["a;b.exe"], Relaunch));
    }

    [TestMethod]
    public void TryAddDefenderProcessExclusions_WithoutAScope_RefusesAnInvalidNameBeforeAnyLaunch()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => ElevationUtilities.TryAddDefenderProcessExclusions([""], Relaunch, new FakeElevation(true, true)));
    }

    [TestMethod]
    public void TryAddDefenderProcessExclusions_UnderAScope_RecordsTheLaunchOnWindowsAndReportsNotAppliedElsewhere()
    {
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        var applied = ElevationUtilities.TryAddDefenderProcessExclusions(
            OneName, Relaunch, new FakeElevation(elevated: true, canSelfElevate: false));

        Assert.AreEqual(OperatingSystem.IsWindows(), applied);
        Assert.HasCount(OperatingSystem.IsWindows() ? 1 : 0, scripted.Launches);
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
            Assert.AreEqual(ImportPrefix + "Defender\\Add-MpPreference -ExclusionProcess 'app.exe' -ErrorAction Stop", launch.Script);
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
            Assert.ThrowsExactly<InvalidOperationException>(() => DefenderExclusionIsolation.ScriptPowerShell());
        }
        finally
        {
            first.Dispose();
        }

        first.Dispose();
        Assert.IsNull(DefenderExclusionSequence.RunnerOverride);
        DefenderExclusionIsolation.ScriptPowerShell().Dispose();
    }

    [GeneratedRegex(@"Defender\\Add-MpPreference")]
    private static partial Regex QualifiedDefenderCommandRegex();

    [GeneratedRegex(@"(?<!Defender\\)Add-MpPreference")]
    private static partial Regex UnqualifiedDefenderCommandRegex();
}
