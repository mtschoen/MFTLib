using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    const string DiagClientLogPath = @"C:\broker-diag-tests\client\broker-diagnostics.ui.log";
    const ulong DiagOwnLogReference = 9101;
    const ulong DiagClientLogReference = 9102;

    static UsnJournalEntry DiagEntry(ulong recordNumber, string fileName = "broker-diagnostics.broker.log") =>
        JournalEntries.Create(recordNumber, 110, fileName);

    // Point diagnostics at a synthetic C:-rooted directory and resolve both log paths to fixed file
    // reference numbers, so no real file system or elevation is involved. The log lines go to a
    // sink that discards them.
    static Func<BrokerDiagnosticsLogFilter?> EnableDiagFilterSeams()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        BrokerDiagnostics.Enable("broker", @"C:\broker-diag-tests");
        BrokerDiagnostics.ClientLogPath = DiagClientLogPath;
        BrokerDiagnostics.Dependencies.InstallWriter(new BrokerDiagnosticsWriter(_ => { }, () => "broker"));
        var ownPath = BrokerDiagnostics.LogPath;
        return () => BrokerDiagnostics.CreateLogFilter(path =>
            string.Equals(path, ownPath, StringComparison.OrdinalIgnoreCase) ? DiagOwnLogReference :
            string.Equals(path, DiagClientLogPath, StringComparison.OrdinalIgnoreCase) ? DiagClientLogReference :
            null);
    }

    static async Task ResetDiagFilterSeamsAsync()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        await BrokerDiagnosticsTestSupport.DrainAsync();
        BrokerDiagnostics.Dependencies.ResetToDefaults();
    }

    [TestMethod]
    public async Task StartWatch_WithDiagnostics_SkipsLogOnlyBatches_StillReportsCaughtUp()
    {
        var createLogFilter = EnableDiagFilterSeams();
        try
        {
            var batches = new[]
            {
                // First batch is nothing but diagnostics-log writes: it must not ship.
                (new[] { DiagEntry(DiagOwnLogReference), DiagEntry(DiagClientLogReference) },
                    new UsnJournalCursor(7UL, 150L)),
                ([DiagEntry(4242, "real.txt")], new UsnJournalCursor(7UL, 160L))
            };
            var host = CreateWatchHost(
                queryCursor: _ => new UsnJournalCursor(7UL, 140L), // journal tip at arm time
                watchDrive: (_, _, _, _) => FiniteWatch(batches),
                createLogFilter: createLogFilter);
            await using var harness = new HostChannelHarness(host);

            // since (7,100) < tip (7,140): backlog
            var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
            var frames = await HostChannelHarness.ReadToEndAsync(pipe);

            // No JournalBatch for the filtered-empty first batch, but the CaughtUp marker still
            // fires because its cursor (7,150) passed the arm-time tip (7,140).
            Assert.AreSequenceEqual([BrokerFrameKind.CaughtUp, BrokerFrameKind.JournalBatch], [.. frames.Select(frame => frame.Kind)]);
            Assert.HasCount(1, frames[1].Entries);
            Assert.AreEqual("real.txt", frames[1].Entries[0].FileName);
        }
        finally
        {
            await ResetDiagFilterSeamsAsync();
        }
    }

    [TestMethod]
    public async Task StartWatch_WithDiagIncludeSelf_ShipsLogEntries()
    {
        var createLogFilter = EnableDiagFilterSeams();
        BrokerDiagnostics.IncludeSelfEntries = true;
        try
        {
            var batches = new[]
            {
                (new[]
                {
                    DiagEntry(DiagOwnLogReference),
                    DiagEntry(DiagClientLogReference),
                    DiagEntry(4242, "real.txt")
                }, new UsnJournalCursor(7UL, 110L))
            };
            // queryCursor returns default: the (7,100) start cursor never matches tip journal identifier 0,
            // so no CaughtUp frame precedes the batch.
            var host = CreateWatchHost(
                queryCursor: _ => default,
                watchDrive: (_, _, _, _) => FiniteWatch(batches),
                createLogFilter: createLogFilter);
            await using var harness = new HostChannelHarness(host);

            var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
            var frames = await HostChannelHarness.ReadToEndAsync(pipe);

            Assert.HasCount(1, frames);
            Assert.AreEqual(BrokerFrameKind.JournalBatch, frames[0].Kind);
            Assert.HasCount(3, frames[0].Entries);
        }
        finally
        {
            await ResetDiagFilterSeamsAsync();
        }
    }

    [TestMethod]
    public async Task StartWatch_WithDiagnosticsOff_ShipsLogEntriesUnfiltered()
    {
        var batches = new[]
        {
            (new[] { DiagEntry(DiagOwnLogReference), DiagEntry(4242, "real.txt") },
                new UsnJournalCursor(7UL, 110L))
        };
        // queryCursor returns default: the (7,100) start cursor never matches tip journal identifier 0,
        // so no CaughtUp frame precedes the batch.
        var host = CreateWatchHost(
            queryCursor: _ => default,
            watchDrive: (_, _, _, _) => FiniteWatch(batches));
        await using var harness = new HostChannelHarness(host);

        var pipe = await harness.OpenWatchChannelAsync('C', new UsnJournalCursor(7UL, 100L));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.HasCount(1, frames);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, frames[0].Kind);
        Assert.HasCount(2, frames[0].Entries);
    }

    [TestMethod]
    public async Task ArmAndScan_WithDiagnostics_CompletesWithoutPayload()
    {
        EnableDiagFilterSeams();
        try
        {
            using var sectionWriter = new RecordingBlockSectionWriter();
            var host = CreateWatchHost(
                queryCursor: _ => new UsnJournalCursor(7UL, 0L),
                readJournal: CatchUpSources.ToTip(new UsnJournalCursor(7UL, 2L),
                    DiagEntry(DiagOwnLogReference), DiagEntry(4242, "real.txt")));
            await using var harness = new HostChannelHarness(host, sectionWriter);

            var pipe = await harness.OpenScanChannelAsync('C');
            var frames = await HostChannelHarness.ReadToEndAsync(pipe);

            Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.JournalBatch, frames);
            var completed = frames.Single(frame => frame.Kind == BrokerFrameKind.ScanCompleted);
            Assert.AreEqual(default(UsnJournalCursor), completed.Cursor);
            Assert.IsEmpty(completed.Entries);
        }
        finally
        {
            await ResetDiagFilterSeamsAsync();
        }
    }
}
