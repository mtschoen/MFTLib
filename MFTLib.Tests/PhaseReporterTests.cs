using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;

namespace MFTLib.Tests;

// The progress lines the samples print. Shared source compiles into each sample; the Direct build is read here.
[TestClass]
public class PhaseReporterTests
{
    [TestMethod]
    public void Report_PrintsTheDriveAndTheDirectoryWhenTheProducerReportsOne()
    {
        using var output = new StringWriter();
        var reporter = new SampleHost.PhaseReporter(output);

        reporter.Report(new IndexScanProgress('D', IndexScanPhase.Enumerating, 7) { CurrentDirectory = @"D:\data" });
        reporter.Report(new IndexScanProgress('D', IndexScanPhase.Enumerating, 9) { CurrentDirectory = @"D:\other" });
        reporter.Report(new IndexScanProgress('D', IndexScanPhase.ParsingMft, 9));

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreSequenceEqual(
            new[] { @"  D: Enumerating: 7 rows in D:\data", "  D: ParsingMft: 9 rows" }, lines);
    }

    [TestMethod]
    public void DriveOpenedReporter_PrintsOneSelfDescribingLinePerReport()
    {
        using var output = new StringWriter();
        var reporter = new SampleProgram.Watch.SampleHost.DriveOpenedReporter(output);

        reporter.Report(new IndexDriveOpened('D', 2, 3));
        reporter.Report(new IndexDriveOpened('C', 1, 3));

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreSequenceEqual(new[] { "D: opened (2 of 3 drives)", "C: opened (1 of 3 drives)" }, lines);
    }
}
