using Microsoft.Extensions.Time.Testing;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;

namespace MFTLib.Tests;

// The exclude-defender verb of the Direct sample. The elevation functions and the PowerShell launch are scripted, so
// no run raises a UAC prompt, starts a real PowerShell or changes real Defender settings. The library reports not
// applied off Windows, so the success cases expect that platform's outcome.
[TestClass]
[DoNotParallelize]
public class DirectExcludeDefenderTests
{
    static readonly int AppliedExitCode = OperatingSystem.IsWindows() ? 0 : 1;

    sealed class ConsoleLog
    {
        public int KeyReads;
        public List<string> Redirects { get; } = [];
        public List<string> Dialogs { get; } = [];
    }

    // Every seam the verb can reach is scripted: the dialog answers as chosen, the console is owned by this process.
    static SampleHost Host(List<string> lines, bool elevated, bool canSelfElevate, bool unattended = false,
        Func<IReadOnlyList<string>, TimeSpan, bool>? tryRunElevated = null, int dialogAnswer = SampleHost.MessageBoxResultOk,
        ConsoleLog? console = null, bool isWindows = true)
    {
        console ??= new ConsoleLog();
        var clock = new FakeTimeProvider();
        return new SampleHost
        {
            _isWindows = () => isWindows,
            _isElevated = () => elevated,
            _canSelfElevate = () => canSelfElevate,
            _tryRunElevated = tryRunElevated ?? ((_, _) => throw new AssertFailedException("No relaunch expected.")),
            _getEnvironmentVariable = name => unattended && name == SampleHost.UnattendedVariableName ? "1" : null,
            _writeLine = lines.Add,
            _timeProvider = clock,
            _messageBeep = _ => true,
            _messageBox = (_, text, _, _) =>
            {
                console.Dialogs.Add(text);
                clock.Advance(SampleHost.AccidentalDismissalInterval);
                return dialogAnswer;
            },
            _ownsConsoleWindow = () => true,
            _readKey = () => console.KeyReads++,
            _acrtIobFunc = _ => IntPtr.Zero,
            _wFreopen = (path, _, _) =>
            {
                console.Redirects.Add(path);
                return IntPtr.Zero;
            }
        };
    }

    [TestMethod]
    public void Run_ElevatedProcess_RunsTheExclusionDirectlyAndReportsIt()
    {
        var lines = new List<string>();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        var console = new ConsoleLog();

        var result = Host(lines, elevated: true, canSelfElevate: true, console: console).RunAndPause([SampleHost.ExcludeDefenderVerb]);

        Assert.AreEqual(AppliedExitCode, result);
        Assert.AreEqual(0, console.KeyReads, "The elevated child owns a new console and must not wait for a key.");
        Assert.AreEqual(1, console.Redirects.Count, "The elevated child writes to output.log like every other elevated run.");
        Assert.AreEqual(0, console.Dialogs.Count, "An already elevated run shows no heads-up.");
        if (OperatingSystem.IsWindows())
        {
            var launch = scripted.Launches.Single();
            Assert.IsFalse(launch.Elevated);
            Assert.IsTrue(launch.Script.Contains($"'{SampleHost.DefenderProcessName}'", StringComparison.Ordinal), launch.Script);
            Assert.IsTrue(lines.Contains($"Windows Defender process exclusion added: {SampleHost.DefenderProcessName}."));
        }
        else
        {
            Assert.AreEqual(0, scripted.Launches.Count);
        }
    }

    [TestMethod]
    public void Run_VerbIsCaseInsensitive()
    {
        var lines = new List<string>();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        Assert.AreEqual(AppliedExitCode, Host(lines, true, false).Run(["Exclude-Defender"]));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Run_UnelevatedButAbleToRelaunch_RelaunchesWithTheVerbAndReportsItsResult(bool relaunchResult)
    {
        var lines = new List<string>();
        var relaunches = new List<(IReadOnlyList<string> Arguments, TimeSpan Timeout)>();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();
        var host = Host(lines, elevated: false, canSelfElevate: true, tryRunElevated: (arguments, timeout) =>
        {
            relaunches.Add((arguments, timeout));
            return relaunchResult;
        });

        var result = host.Run([SampleHost.ExcludeDefenderVerb]);

        Assert.AreEqual(OperatingSystem.IsWindows() && relaunchResult ? 0 : 1, result);
        Assert.AreEqual(0, scripted.Launches.Count);
        if (OperatingSystem.IsWindows())
        {
            var (arguments, timeout) = relaunches.Single();
            CollectionAssert.AreEqual(new[] { SampleHost.ExcludeDefenderVerb }, arguments.ToArray());
            Assert.AreEqual(TimeSpan.FromSeconds(60), timeout);
        }
    }

    [DataTestMethod]
    [DataRow(true, DisplayName = "self-elevating route")]
    [DataRow(false, DisplayName = "PowerShell fallback route")]
    public void Run_UnelevatedAndTheHeadsUpIsCancelled_MakesNoLibraryCallAndFails(bool canSelfElevate)
    {
        var lines = new List<string>();
        var console = new ConsoleLog();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();
        var host = Host(lines, elevated: false, canSelfElevate, dialogAnswer: SampleHost.MessageBoxResultCancel, console: console);

        var result = host.Run([SampleHost.ExcludeDefenderVerb]);

        Assert.AreEqual(1, result);
        Assert.AreEqual(1, console.Dialogs.Count);
        Assert.IsTrue(console.Dialogs[0].Contains("Defender", StringComparison.Ordinal), console.Dialogs[0]);
        Assert.IsFalse(console.Dialogs[0].Contains("Reading a volume's MFT", StringComparison.Ordinal), console.Dialogs[0]);
        Assert.AreEqual(0, scripted.Launches.Count);
        Assert.IsTrue(lines.Contains("Cancelled: no elevation was requested and no Windows Defender exclusion was added."));
    }

    [TestMethod]
    public void Run_UnelevatedAndAttended_ShowsTheHeadsUpOnceBeforeTheLibraryCall()
    {
        var lines = new List<string>();
        var console = new ConsoleLog();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        Host(lines, elevated: false, canSelfElevate: false, console: console).Run([SampleHost.ExcludeDefenderVerb]);

        Assert.AreEqual(1, console.Dialogs.Count);
        Assert.AreEqual(OperatingSystem.IsWindows() ? 1 : 0, scripted.Launches.Count);
    }

    [TestMethod]
    public void Run_NotWindows_ShowsNoHeadsUpAndAddsNothing()
    {
        var lines = new List<string>();
        var console = new ConsoleLog();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        var result = Host(lines, elevated: false, canSelfElevate: true, console: console, isWindows: false, tryRunElevated: (_, _) => false).Run([SampleHost.ExcludeDefenderVerb]);

        Assert.AreEqual(1, result);
        Assert.AreEqual(0, console.Dialogs.Count);
        Assert.AreEqual(0, scripted.Launches.Count);
    }

    [TestMethod]
    public void Run_UnelevatedAndUnableToRelaunch_UsesTheElevatedPowerShellAndReportsAFailure()
    {
        var lines = new List<string>();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell(succeeds: false);

        var result = Host(lines, elevated: false, canSelfElevate: false).Run([SampleHost.ExcludeDefenderVerb]);

        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Contains("The Windows Defender process exclusion was not added."));
        if (OperatingSystem.IsWindows())
        {
            Assert.IsTrue(scripted.Launches.Single().Elevated);
        }
    }

    [TestMethod]
    public void Run_UnattendedAndUnelevated_SkipsElevationWithoutAnyLaunch()
    {
        var lines = new List<string>();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        var result = Host(lines, elevated: false, canSelfElevate: true, unattended: true).Run([SampleHost.ExcludeDefenderVerb]);

        Assert.AreEqual(1, result);
        Assert.AreEqual(0, scripted.Launches.Count);
        Assert.IsTrue(lines.Any(line => line.Contains("Running unattended (MFTLIB_SAMPLE_UNATTENDED=1)", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Run_UnattendedButElevated_StillRunsTheExclusion()
    {
        var lines = new List<string>();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        Assert.AreEqual(AppliedExitCode, Host(lines, elevated: true, canSelfElevate: true, unattended: true).Run([SampleHost.ExcludeDefenderVerb]));
    }

    [TestMethod]
    public void Run_VerbWithAnExtraArgument_IsAUsageError()
    {
        var lines = new List<string>();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        var result = Host(lines, elevated: true, canSelfElevate: true).Run([SampleHost.ExcludeDefenderVerb, "D"]);

        Assert.AreEqual(2, result);
        Assert.AreEqual(0, scripted.Launches.Count);
        Assert.IsTrue(lines.Any(line => line.Contains("exclude-defender", StringComparison.Ordinal)));
    }
}
