using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.SampleHostContract;

// The shared contract against SampleProgram.Watch's copy of SampleHost, which gates its broker launch behind the same dialog.
[TestClass]
[DoNotParallelize]
public sealed class WatchSharedSampleHostTests : SharedSampleHostTests
{
    internal override SampleHostSubject CreateSubject(TextWriter output, TextReader input, FakeTimeProvider clock, Action elevated) =>
        new WatchSampleHostSubject(output, input, clock, elevated);

    [TestMethod]
    public void DriveOpenedReporter_PrintsOneSelfDescribingLinePerReport()
    {
        using var output = new StringWriter();
        var reporter = new SampleProgram.Watch.SampleHost.DriveOpenedReporter(output);

        reporter.Report(new MFTLib.Index.IndexDriveOpened('D', 2, 3));
        reporter.Report(new MFTLib.Index.IndexDriveOpened('C', 1, 3));

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreSequenceEqual(["D: opened (2 of 3 drives)", "C: opened (1 of 3 drives)"], lines);
    }
}
