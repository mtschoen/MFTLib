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
    public void PhaseReporter_PrintsTheDriveAndTheDirectoryWhenTheProducerReportsOne()
    {
        using var output = new StringWriter();
        var reporter = Probe.CreateReporter(output);

        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Enumerating, 7) { CurrentDirectory = @"D:\data" });
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Enumerating, 9) { CurrentDirectory = @"D:\other" });
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.ParsingMft, 9));

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreSequenceEqual(
            [@"  D: Enumerating: 7 rows in D:\data", "  D: ParsingMft: 9 rows"], lines);
    }

    [TestMethod]
    public void PhaseReporter_PrintsAgainEveryIntervalOfRowsWithinAPhaseAndNeverByTime()
    {
        using var output = new StringWriter();
        var probe = Probe;
        var reporter = probe.CreateReporter(output);
        var interval = probe.ProgressRowInterval;

        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.ParsingMft, 0) { ExpectedRowCount = 3 * interval });
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.ParsingMft, interval - 1) { ExpectedRowCount = 3 * interval });
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.ParsingMft, interval) { ExpectedRowCount = 3 * interval });
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.ParsingMft, interval + 5) { ExpectedRowCount = 3 * interval });
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.ParsingMft, 2 * interval) { ExpectedRowCount = 3 * interval });

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreSequenceEqual(
            [
                $"  D: ParsingMft: 0 rows of {3 * interval}",
                $"  D: ParsingMft: {interval} rows of {3 * interval}",
                $"  D: ParsingMft: {2 * interval} rows of {3 * interval}"
            ], lines);
    }

    [TestMethod]
    public void PhaseReporter_KeepsEachDrivesPhaseAndCompletionWhenDriveProgressInterleaves()
    {
        using var output = new StringWriter();
        var reporter = Probe.CreateReporter(output);

        reporter.Report(new IndexScanProgress(new VolumeKey('C'), IndexScanPhase.ParsingMft, 5));
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.ParsingMft, 5));
        reporter.Report(new IndexScanProgress(new VolumeKey('C'), IndexScanPhase.Enumerating, 5));
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Enumerating, 5));
        reporter.Report(new IndexScanProgress(new VolumeKey('C'), IndexScanPhase.Finished, 5));
        reporter.Report(new IndexScanProgress(new VolumeKey('D'), IndexScanPhase.Finished, 5));

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreSequenceEqual(
            [
                "  C: ParsingMft: 5 rows", "  D: ParsingMft: 5 rows",
                "  C: Enumerating: 5 rows", "  D: Enumerating: 5 rows",
                "  C: Finished: 5 rows", "  D: Finished: 5 rows"
            ], lines);
    }
}
