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
        var lines = new List<string>();
        var reporter = new SampleHost.PhaseReporter(lines.Add);

        reporter.Report(new IndexScanProgress('D', IndexScanPhase.Enumerating, 7) { CurrentDirectory = @"D:\data" });
        reporter.Report(new IndexScanProgress('D', IndexScanPhase.Enumerating, 9) { CurrentDirectory = @"D:\other" });
        reporter.Report(new IndexScanProgress('D', IndexScanPhase.ParsingMft, 9));

        CollectionAssert.AreEqual(
            new[] { @"  D: Enumerating: 7 rows in D:\data", "  D: ParsingMft: 9 rows" }, lines);
    }

    [TestMethod]
    public void DriveOpenedReporter_PrintsOneSelfDescribingLinePerReport()
    {
        var lines = new List<string>();
        var reporter = new SampleHost.DriveOpenedReporter(lines.Add);

        reporter.Report(new IndexDriveOpened('D', 2, 3));
        reporter.Report(new IndexDriveOpened('C', 1, 3));

        CollectionAssert.AreEqual(new[] { "D: opened (2 of 3 drives)", "C: opened (1 of 3 drives)" }, lines);
    }
}
