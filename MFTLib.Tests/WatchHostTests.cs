using MFTLibTestExtensions;
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
    public void Run_NotElevated_SelfElevateSucceeds_ReturnsZero()
    {
        var lines = new List<string>();
        var scanner = new SampleHost
        {
            _elevationNeed = _ => ElevationNeed.SelfElevate,
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _tryRunElevated = (_, _) => true,
            _writeLine = lines.Add
        };
        WatchNoticeSupport.AcknowledgeDeliberately(scanner);

        var result = scanner.Run([]);
        Assert.AreEqual(0, result);
    }

    [DataTestMethod]
    [DataRow("C", DisplayName = "default-drive")]
    [DataRow("", DisplayName = "empty-drive")]
    [DataRow("x&echo(123", DisplayName = "cmd-ampersand")]
    [DataRow("x;echo(123);#", DisplayName = "powershell-separator")]
    [DataRow("$(Get-Date)", DisplayName = "powershell-subexpression")]
    [DataRow("%USERNAME%", DisplayName = "cmd-variable")]
    [DataRow("$env:USERNAME", DisplayName = "powershell-variable")]
    [DataRow("`n", DisplayName = "backtick-n")]
    [DataRow("a'b", DisplayName = "single-quote")]
    [DataRow("say\"hi", DisplayName = "embedded-quote")]
    [DataRow(@"C:\spaced directory\", DisplayName = "trailing-backslash")]
    public void Run_NotElevated_CannotSelfElevate_PrintsEachArgumentVerbatimOnItsOwnLine(string value)
    {
        string[] arguments = ["watch", "--cache-directory", value];
        var lines = new List<string>();
        var scanner = new SampleHost
        {
            _elevationNeed = _ => ElevationNeed.SelfElevate,
            _isElevated = () => false,
            _canSelfElevate = () => false,
            _getEnvironmentVariable = _ => null,
            _getProcessPath = () => @"C:pp\SampleProgram.Watch.exe",
            _writeLine = lines.Add
        };

        var result = scanner.Run(arguments);
        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.Contains("AUTOMATIC ELEVATION FAILED")));
        Assert.IsTrue(lines.Any(line => line.Contains(@"C:pp\SampleProgram.Watch.exe")));

        var headerIndex = lines.FindIndex(line => line.StartsWith($"Arguments ({arguments.Length})", StringComparison.Ordinal));
        Assert.IsTrue(headerIndex >= 0, "argument count header missing");
        var listed = lines.Skip(headerIndex + 1).Take(arguments.Length).ToArray();
        var expected = arguments.Select(argument => argument.Length == 0 ? "  <empty>" : "  " + argument).ToArray();
        CollectionAssert.AreEqual(expected, listed);
        Assert.AreEqual(headerIndex + arguments.Length + 2, lines.Count, "only the closing rule may follow the arguments");
    }

    [TestMethod]
    public void Run_NotElevated_CanSelfElevateButFails_PrintsFailureAndReturnsOne()
    {
        var lines = new List<string>();
        var scanner = new SampleHost
        {
            _elevationNeed = _ => ElevationNeed.SelfElevate,
            _isElevated = () => false,
            _canSelfElevate = () => true,
            _tryRunElevated = (_, _) => false,
            _getProcessPath = () => "/some/path",
            _writeLine = lines.Add
        };
        WatchNoticeSupport.AcknowledgeDeliberately(scanner);

        var result = scanner.Run(["C"]);
        Assert.AreEqual(1, result);
        Assert.IsTrue(lines.Any(line => line.Contains("AUTOMATIC ELEVATION FAILED")));
    }

    // --- Run: elevated paths ---

    [TestMethod]
    public void Run_Elevated_NoArgs_UsesDefaultDriveG()
    {
        var scannedDrives = new List<string>();
        var scanner = ElevatedScanner(scannedDrives, new List<string>());

        scanner.Run([]);

        CollectionAssert.AreEqual(new[] { "G" }, scannedDrives);
    }

    [TestMethod]
    public void Run_Elevated_WithArgs_ScansSpecifiedDrives()
    {
        var scannedDrives = new List<string>();
        var scanner = ElevatedScanner(scannedDrives, new List<string>());

        scanner.Run(["C", "D"]);

        CollectionAssert.AreEqual(new[] { "C", "D" }, scannedDrives);
    }

    [TestMethod]
    public void Run_Elevated_RedirectsStdout()
    {
        uint capturedIndex = 0;
        string? redirectedPath = null;
        var scanner = ElevatedScanner(new List<string>(), new List<string>());
        scanner._acrtIobFunc = index =>
        {
            capturedIndex = index;
            return new IntPtr(42);
        };
        scanner._wFreopen = (path, _, _) =>
        {
            redirectedPath = path;
            return IntPtr.Zero;
        };

        scanner.Run(["T"]);

        Assert.AreEqual(1u, capturedIndex);
        Assert.IsNotNull(redirectedPath);
        Assert.IsTrue(redirectedPath!.EndsWith("output.log", StringComparison.Ordinal));
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

    // An elevated run that needs elevation itself, over a drive resolver that records each drive and then fails, so
    // no volume or broker is touched.
    static SampleHost ElevatedScanner(List<string> scannedDrives, List<string> lines)
    {
        return new SampleHost
        {
            _elevationNeed = _ => ElevationNeed.SelfElevate,
            _isElevated = () => true,
            _acrtIobFunc = _ => IntPtr.Zero,
            _wFreopen = (_, _, _) => IntPtr.Zero,
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
