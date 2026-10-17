using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Watch;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class WatchHostTests
{
    // A session that never launches: every test here fails before a broker would be needed.
    static BrokerSession UnusedSession() =>
        BrokerTestHarness.CreateSession(_ => throw new AssertFailedException("No broker may launch in this test."));

    // --- Run: elevation paths ---

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
            _getProcessPath = () => @"C:\tools\SampleProgram.Watch.exe",
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
            _canSelfElevate = () => throw new AssertFailedException("A broker launch must not self-elevate.")
        };

        var result = scanner.RunWithElevation(["scan", "C"], ElevationNeed.BrokerLaunch, () => 0);

        Assert.AreEqual(0, result);
        Assert.IsTrue(title!.Contains("SampleProgram", StringComparison.Ordinal));
        Assert.IsTrue(text!.Contains("UAC", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("administrator rights", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("launches its broker elevated", StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow("scan C", DisplayName = "scan")]
    [DataRow("", DisplayName = "default-watch-verb")]
    public void Run_Unattended_NeverLaunchesTheBroker(string commandLine)
    {
        var arguments = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var brokerLaunched = false;
        var scanner = new SampleHost
        {
            _listDrives = TwoDrives,
            _cacheDirectory = @"C:\unused-cache",
            _isElevated = () => false,
            _createBrokerSession = () =>
            {
                brokerLaunched = true;
                throw new AssertFailedException("An unattended run must not launch the broker.");
            },
            _getProcessPath = () => @"C:\tools\SampleProgram.Watch.exe",
            _writeLine = lines.Add
        };
        WatchNoticeSupport.AcknowledgeDeliberately(scanner);
        scanner._getEnvironmentVariable = name => name == SampleHost.UnattendedVariableName ? "1" : null;

        var result = scanner.Run(arguments);

        Assert.AreEqual(1, result);
        Assert.IsFalse(brokerLaunched, "An unattended run must not launch the broker.");
        Assert.IsTrue(lines.Any(line => line.Contains("Running unattended (MFTLIB_SAMPLE_UNATTENDED=1): elevation skipped")));
        Assert.IsTrue(lines.Any(line => line.Contains("AUTOMATIC ELEVATION FAILED")));
        Assert.IsTrue(lines.Any(line => line.StartsWith($"Arguments ({arguments.Length})", StringComparison.Ordinal)));
        foreach (var argument in arguments)
        {
            Assert.IsTrue(lines.Contains($"  {argument}"), $"Argument {argument} was not listed.");
        }
    }

    // --- Run: elevated paths ---

    [TestMethod]
    public void Run_Elevated_NoArgs_CoversTheMachinesNtfsFixedDrives()
    {
        var scannedDrives = new List<string>();
        var scanner = ElevatedScanner(scannedDrives, new List<string>());

        scanner.Run([]);

        CollectionAssert.AreEqual(new[] { "C", "D" }, scannedDrives);
    }

    [TestMethod]
    public void Run_Elevated_WithArgs_ScansSpecifiedDrives()
    {
        var scannedDrives = new List<string>();
        var scanner = ElevatedScanner(scannedDrives, new List<string>());

        scanner.Run(["C", "D"]);

        CollectionAssert.AreEqual(new[] { "C", "D" }, scannedDrives);
    }

    // --- Entry point ---

    // The entry point builds a SampleHost with every default seam, so it must be given a command line the parser
    // refuses: any scan would launch the elevated broker.
    [TestMethod]
    public void SampleProgramWatch_EntryPoint_UnknownOption_PrintsUsageAndReturnsTwo()
    {
        var entryPoint = typeof(SampleHost).Assembly.EntryPoint!;
        var originalOut = Console.Out;
        using var captured = new StringWriter();
        object? exitCode;
        try
        {
            Console.SetOut(captured);
            exitCode = entryPoint.Invoke(null, [new[] { "--no-such-option" }]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.AreEqual(2, exitCode);
        var output = captured.ToString();
        Assert.IsTrue(output.Contains("Unknown option --no-such-option.", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains("Usage:", StringComparison.Ordinal));
    }

    // --- Helpers ---

    static IReadOnlyList<(string RootDirectory, bool IsIndexable, string? Reason)> TwoDrives() =>
        [WatchFirstRunTests.Facts(@"C:\"), WatchFirstRunTests.Facts(@"D:\")];

    // An elevated run over a drive resolver that records each drive and then fails, so no volume or broker is touched.
    static SampleHost ElevatedScanner(List<string> scannedDrives, List<string> lines)
    {
        return new SampleHost
        {
            _listDrives = TwoDrives,
            _isElevated = () => true,
            _createBrokerSession = UnusedSession,
            _resolveDrive = letter =>
            {
                scannedDrives.Add(letter);
                throw new IOException("Mock: drive not available");
            },
            _writeLine = lines.Add
        };
    }
}
