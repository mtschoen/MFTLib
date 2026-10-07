using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleProgram.Direct;

namespace MFTLib.Tests;

// The phase line both samples print. Shared source compiles into each sample; the Direct build is read here.
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
}
