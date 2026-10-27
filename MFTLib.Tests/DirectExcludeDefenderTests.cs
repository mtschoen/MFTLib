using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;

namespace MFTLib.Tests;

// The exclude-defender verb of the Direct sample. The elevation functions and the PowerShell launch are scripted, so
// no run raises a UAC prompt, starts a real PowerShell or changes real Defender settings. The library reports not
// applied off Windows, so the success cases expect that platform's outcome.
[TestClass]
[DoNotParallelize]
public sealed class DirectExcludeDefenderTests : IDisposable
{
    static readonly int AppliedExitCode = OperatingSystem.IsWindows() ? 0 : 1;

    sealed class ConsoleLog
    {
        public int KeyReads;
        public List<string> Dialogs { get; } = [];
    }

    TempDirectory _directoryOwner = null!;
    string _directory = null!;
    string? _previousUnattended;

    [TestInitialize]
    public void SetUp()
    {
        _directoryOwner = new TempDirectory("defender-tests");
        _directory = _directoryOwner.Path;
        _previousUnattended = Environment.GetEnvironmentVariable(SampleHost.UnattendedVariableName);
        Environment.SetEnvironmentVariable(SampleHost.UnattendedVariableName, null);
    }

    [TestCleanup]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(SampleHost.UnattendedVariableName, _previousUnattended);
        _directoryOwner.Dispose();
    }

    public void Dispose() => TearDown();

    sealed class CountingReader(ConsoleLog console) : TextReader
    {
        public override int Read()
        {
            console.KeyReads++;
            return 'x';
        }
    }

    // Every seam the verb can reach is scripted: the dialog answers as chosen, the console is owned by this process.
    SampleHost Host(List<string> lines, bool elevated, bool canSelfElevate, bool unattended = false,
        Func<IReadOnlyList<string>, TimeSpan, bool>? tryRunElevated = null, int dialogAnswer = SampleHost.MessageBoxResultOk,
        ConsoleLog? console = null, bool isWindows = true)
    {
        console ??= new ConsoleLog();
        if (unattended)
        {
            Environment.SetEnvironmentVariable(SampleHost.UnattendedVariableName, "1");
        }
        var clock = new FakeTimeProvider();
        return new SampleHost(new LineCollectingWriter(lines), new CountingReader(console), _directory, clock)
        {
            _isWindows = () => isWindows,
            _isElevated = () => elevated,
            _canSelfElevate = () => canSelfElevate,
            _tryRunElevated = tryRunElevated ?? ((_, _) => throw new AssertFailedException("No relaunch expected.")),
            _messageBeep = _ => true,
            _messageBox = (_, text, _, _) =>
            {
                console.Dialogs.Add(text);
                clock.Advance(SampleHost.AccidentalDismissalInterval);
                return dialogAnswer;
            }
        };
    }

    [TestMethod]
    public void Run_ElevatedProcess_RunsTheExclusionDirectlyAndReportsIt()
    {
        var lines = new List<string>();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        var console = new ConsoleLog();

        var host = Host(lines, elevated: true, canSelfElevate: true, console: console);
        var result = host.RunAndPause([SampleHost.ExcludeDefenderVerb], ownsWindow: true);

        Assert.AreEqual(AppliedExitCode, result);
        Assert.AreEqual(0, console.KeyReads, "The elevated child owns a new console and must not wait for a key.");
        Assert.IsEmpty(console.Dialogs, "An already elevated run shows no heads-up.");
        var log = File.ReadAllText(host.OutputLogPath);
        if (OperatingSystem.IsWindows())
        {
            var launch = scripted.Launches.Single();
            Assert.IsFalse(launch.IsElevated);
            Assert.IsTrue(launch.Script.Contains($"'{SampleHost.DefenderProcessName}'", StringComparison.Ordinal), launch.Script);
            Assert.Contains($"Windows Defender process exclusion added: {SampleHost.DefenderProcessName}.", log);
        }
        else
        {
            Assert.IsEmpty(scripted.Launches);
        }
    }

    [TestMethod]
    public void Run_VerbIsCaseInsensitive()
    {
        var lines = new List<string>();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        Assert.AreEqual(AppliedExitCode, Host(lines, true, false).Run(["Exclude-Defender"]));
    }

    [TestMethod]
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
        Assert.IsEmpty(scripted.Launches);
        if (OperatingSystem.IsWindows())
        {
            var (arguments, timeout) = relaunches.Single();
            Assert.AreSequenceEqual([SampleHost.ExcludeDefenderVerb], [.. arguments]);
            Assert.AreEqual(TimeSpan.FromSeconds(60), timeout);
        }
    }

    [TestMethod]
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
        Assert.HasCount(1, console.Dialogs);
        Assert.IsTrue(console.Dialogs[0].Contains("Defender", StringComparison.Ordinal), console.Dialogs[0]);
        Assert.IsFalse(console.Dialogs[0].Contains("Reading a volume's MFT", StringComparison.Ordinal), console.Dialogs[0]);
        Assert.IsEmpty(scripted.Launches);
        Assert.Contains("Cancelled: no elevation was requested and no Windows Defender exclusion was added.", lines);
    }

    [TestMethod]
    public void Run_UnelevatedAndAttended_ShowsTheHeadsUpOnceBeforeTheLibraryCall()
    {
        var lines = new List<string>();
        var console = new ConsoleLog();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        Host(lines, elevated: false, canSelfElevate: false, console: console).Run([SampleHost.ExcludeDefenderVerb]);

        Assert.HasCount(1, console.Dialogs);
        Assert.HasCount(OperatingSystem.IsWindows() ? 1 : 0, scripted.Launches);
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void Run_NotWindows_ShowsNoHeadsUpAndAddsNothing()
    {
        var lines = new List<string>();
        var console = new ConsoleLog();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        var result = Host(lines, elevated: false, canSelfElevate: true, tryRunElevated: (_, _) => false, console: console, isWindows: false).Run([SampleHost.ExcludeDefenderVerb]);

        Assert.AreEqual(1, result);
        Assert.IsEmpty(console.Dialogs);
        Assert.IsEmpty(scripted.Launches);
    }

    [TestMethod]
    public void Run_UnelevatedAndUnableToRelaunch_UsesTheElevatedPowerShellAndReportsAFailure()
    {
        var lines = new List<string>();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell(succeeds: false);

        var result = Host(lines, elevated: false, canSelfElevate: false).Run([SampleHost.ExcludeDefenderVerb]);

        Assert.AreEqual(1, result);
        Assert.Contains("The Windows Defender process exclusion was not added.", lines);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsTrue(scripted.Launches.Single().IsElevated);
        }
    }

    [TestMethod]
    public void Run_UnattendedAndUnelevated_SkipsElevationWithoutAnyLaunch()
    {
        var lines = new List<string>();
        using var scripted = DefenderExclusionIsolation.ScriptPowerShell();

        var result = Host(lines, elevated: false, canSelfElevate: true, unattended: true).Run([SampleHost.ExcludeDefenderVerb]);

        Assert.AreEqual(1, result);
        Assert.IsEmpty(scripted.Launches);
        Assert.Contains(line => line.Contains("Running unattended (MFTLIB_SAMPLE_UNATTENDED=1)", StringComparison.Ordinal), lines);
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
        Assert.IsEmpty(scripted.Launches);
        Assert.Contains(line => line.Contains("exclude-defender", StringComparison.Ordinal), lines);
    }
}
