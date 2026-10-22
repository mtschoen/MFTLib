using MFTLib.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Watch;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class WatchHostTests
{
    // --- Run: elevation paths ---

    [TestMethod]
    public void RunWithElevation_BrokerLaunch_NotElevated_DialogNamesTheBrokerLaunchItPrecedes()
    {
        using var attended = new UnattendedEnvironmentScope(null);
        string? text = null;
        string? title = null;
        var clock = new FakeTimeProvider();
        var scanner = new SampleHost(TextWriter.Null, TextReader.Null, clock)
        {
            _isElevated = () => false,
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

    [TestMethod]
    [DataRow("scan C", DisplayName = "scan")]
    [DataRow("", DisplayName = "default-watch-verb")]
    public void Run_Unattended_NeverLaunchesTheBroker(string commandLine)
    {
        if (string.IsNullOrEmpty(commandLine) && !OperatingSystem.IsWindows())
        {
            return;
        }

        using var unattended = new UnattendedEnvironmentScope("1");
        var arguments = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var scanner = new SampleHost(new LineCollectingWriter(lines), TextReader.Null, new FakeTimeProvider())
        {
            _isElevated = () => false
        };
        WatchNoticeSupport.AcknowledgeDeliberately(scanner);

        var result = scanner.Run(arguments);

        Assert.AreEqual(1, result);
        Assert.Contains(line => line.Contains("Running unattended (MFTLIB_SAMPLE_UNATTENDED=1): elevation skipped"), lines);
        Assert.Contains(line => line.Contains("AUTOMATIC ELEVATION FAILED"), lines);
        Assert.Contains(line => line.StartsWith($"Arguments ({arguments.Length})", StringComparison.Ordinal), lines);
        foreach (var argument in arguments)
        {
            Assert.Contains($"  {argument}", lines, $"Argument {argument} was not listed.");
        }
    }

    // The entry point composes the real console host, so it must be given a command line the parser refuses: any scan
    // would launch the elevated broker.
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
}
