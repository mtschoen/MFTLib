using MFTLib.Index;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.SampleHostContract;

// The window hold and the progress lines, which both samples compile from SampleProgram.Shared.
partial class SharedSampleHostTests
{
    [TestMethod]
    public void PauseBeforeExit_OwnedWindow_PromptsThenReadsOneKeyAndLeavesTheStreamsOpen()
    {
        using var output = new StringWriter();
        using var input = new StringReader("xy");
        var subject = CreateSubject(output, input, new FakeTimeProvider(), () => { });

        subject.PauseBeforeExit(ownsWindow: true);

        Assert.Contains(subject.PausePrompt, output.ToString());
        Assert.AreEqual('y', (char)input.Read());
        output.WriteLine("borrowed writer remains open");
    }

    [TestMethod]
    public void PauseBeforeExit_WindowNotOwned_DoesNotWait()
    {
        using var output = new StringWriter();
        using var input = new StringReader("x");
        var subject = CreateSubject(output, input, new FakeTimeProvider(), () => { });

        subject.PauseBeforeExit(ownsWindow: false);

        Assert.AreEqual('x', (char)input.Read());
        Assert.IsFalse(output.ToString().Contains(subject.PausePrompt, StringComparison.Ordinal));
    }

    [TestMethod]
    public void PauseBeforeExit_Unattended_DoesNotWaitEvenOnAnOwnedWindow()
    {
        Environment.SetEnvironmentVariable(_unattendedVariableName, "1");
        using var output = new StringWriter();
        using var input = new StringReader("x");
        var subject = CreateSubject(output, input, new FakeTimeProvider(), () => { });

        subject.PauseBeforeExit(ownsWindow: true);

        Assert.AreEqual('x', (char)input.Read());
        Assert.IsFalse(output.ToString().Contains(subject.PausePrompt, StringComparison.Ordinal));
    }

    [TestMethod]
    public void PhaseReporter_PrintsTheRootAndTheDirectoryWhenTheProducerReportsOne()
    {
        using var output = new StringWriter();
        var reporter = Probe.CreateReporter(output);

        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Enumerating, 7) { CurrentDirectory = @"D:\data" });
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Enumerating, 9) { CurrentDirectory = @"D:\other" });
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Scanning, 9));

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreSequenceEqual(
            [@"  D: Enumerating: 7 rows in D:\data", "  D: Scanning: 9 records"], lines);
    }

    [TestMethod]
    public void PhaseReporter_PrintsAgainEveryIntervalOfRowsWithinAPhaseAndNeverByTime()
    {
        using var output = new StringWriter();
        var probe = Probe;
        var reporter = probe.CreateReporter(output);
        var interval = probe.ProgressRowInterval;

        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Scanning, 0) { ExpectedRowCount = 3 * interval });
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Scanning, interval - 1) { ExpectedRowCount = 3 * interval });
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Scanning, interval) { ExpectedRowCount = 3 * interval });
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Scanning, interval + 5) { ExpectedRowCount = 3 * interval });
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Scanning, 2 * interval) { ExpectedRowCount = 3 * interval });

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreSequenceEqual(
            [
                $"  D: Scanning: 0 records of {3 * interval}",
                $"  D: Scanning: {interval} records of {3 * interval}",
                $"  D: Scanning: {2 * interval} records of {3 * interval}"
            ], lines);
    }

    [TestMethod]
    public void PhaseReporter_KeepsEachRootsPhaseAndCompletionWhenRootProgressInterleaves()
    {
        using var output = new StringWriter();
        var reporter = Probe.CreateReporter(output);

        reporter.Report(new IndexScanProgress(new VolumeKey('C'), IndexScanPhase.Scanning, 5));
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Scanning, 5));
        reporter.Report(new IndexScanProgress(new VolumeKey('C'), IndexScanPhase.Enumerating, 5));
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Enumerating, 5));
        reporter.Report(new IndexScanProgress(new VolumeKey('C'), IndexScanPhase.Finished, 5));
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Finished, 5));

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreSequenceEqual(
            [
                "  C: Scanning: 5 records", "  D: Scanning: 5 records",
                "  C: Enumerating: 5 rows", "  D: Enumerating: 5 rows",
                "  C: Finished: 5 rows", "  D: Finished: 5 rows"
            ], lines);
    }

    [TestMethod]
    public void PhaseReporter_PrintsRecordsWithoutATotalAndIndexingWithoutACount()
    {
        using var output = new StringWriter();
        var reporter = Probe.CreateReporter(output);

        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Scanning, 4));
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Indexing, 0));

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreSequenceEqual(["  D: Scanning: 4 records", "  D: Indexing"], lines);
    }
}
