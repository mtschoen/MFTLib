using System.Buffers;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerHostTests
{
    const string DiagClientLogPath = @"C:\broker-diag-tests\client\broker-diagnostics.log";
    const ulong DiagOwnLogReference = 9101;
    const ulong DiagClientLogReference = 9102;

    static string DiagOwnLogPath => Path.Combine(BrokerDiagnostics.LogDirectory, "broker-diagnostics.log");

    static UsnJournalEntry DiagEntry(ulong recordNumber, string fileName = "broker-diagnostics.log") =>
        JournalEntryFactory.Create(recordNumber, 110, fileName);

    // Point diagnostics at a synthetic C:-rooted directory and resolve both log paths to
    // fixed file reference numbers, so no real file system or elevation is involved.
    static void EnableDiagFilterSeams()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        BrokerDiagnostics.LogDirectory = @"C:\broker-diag-tests";
        BrokerDiagnostics.ClientLogPath = DiagClientLogPath;
        BrokerDiagnosticsLogFilter._resolveFileReference = path =>
            string.Equals(path, DiagOwnLogPath, StringComparison.OrdinalIgnoreCase) ? DiagOwnLogReference :
            string.Equals(path, DiagClientLogPath, StringComparison.OrdinalIgnoreCase) ? DiagClientLogReference :
            null;
    }

    static void ResetDiagFilterSeams()
    {
        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        BrokerDiagnostics.ResetToDefaults();
        BrokerDiagnosticsLogFilter.ResetToDefaults();
        BrokerDiagnostics.LogDirectory = Path.GetTempPath();
    }

    [TestMethod]
    public async Task StartWatch_WithDiagnostics_SkipsLogOnlyBatches_StillReportsCaughtUp()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        EnableDiagFilterSeams();
        try
        {
            var batches = new[]
            {
                // First batch is nothing but diagnostics-log writes: it must not ship.
                (new[] { DiagEntry(DiagOwnLogReference), DiagEntry(DiagClientLogReference) },
                    new UsnJournalCursor(7UL, 150L)),
                ([DiagEntry(4242, "real.txt")], new UsnJournalCursor(7UL, 160L))
            };
            var host = CreateHost(
                _ => new UsnJournalCursor(7UL, 140L), // journal tip at arm time
                (_, _, _) => [],
                (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
                (_, _, cancellationToken) => FakeWatch(batches, cancellationToken));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteStartWatch(request, 1, "C:7:100:1"); // since (7,100) < tip (7,140): backlog
            await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
            await clientSide.FlushAsync(CancellationToken.None);

            var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

            // No JournalBatch for the filtered-empty first batch, but the CaughtUp marker
            // still fires because its cursor (7,150) passed the arm-time tip (7,140).
            var first = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
            Assert.AreEqual(BrokerFrameKind.CaughtUp, first.Kind);
            var second = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
            Assert.AreEqual(BrokerFrameKind.JournalBatch, second.Kind);
            Assert.AreEqual(1, second.Entries.Length);
            Assert.AreEqual("real.txt", second.Entries[0].FileName);

            await cts.CancelAsync();
            await serveTask;
        }
        finally
        {
            ResetDiagFilterSeams();
        }
    }

    [TestMethod]
    public async Task StartWatch_WithDiagIncludeSelf_ShipsLogEntries()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        EnableDiagFilterSeams();
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
            // queryCursor returns default: the (7,100) start cursor never matches tip
            // journal id 0, so no CaughtUp frame precedes the batch.
            var host = CreateHost(
                _ => default,
                (_, _, _) => [],
                (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
                (_, _, cancellationToken) => FakeWatch(batches, cancellationToken));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteStartWatch(request, 1, "C:7:100:1");
            await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
            await clientSide.FlushAsync(CancellationToken.None);

            var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

            var batch = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
            Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);
            Assert.AreEqual(3, batch.Entries.Length);

            await cts.CancelAsync();
            await serveTask;
        }
        finally
        {
            ResetDiagFilterSeams();
        }
    }

    [TestMethod]
    public async Task StartWatch_WithDiagnosticsOff_ShipsLogEntriesUnfiltered()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var batches = new[]
        {
            (new[] { DiagEntry(DiagOwnLogReference), DiagEntry(4242, "real.txt") },
                new UsnJournalCursor(7UL, 110L))
        };
        // queryCursor returns default: the (7,100) start cursor never matches tip
        // journal id 0, so no CaughtUp frame precedes the batch.
        var host = CreateHost(
            _ => default,
            (_, _, _) => [],
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            (_, _, cancellationToken) => FakeWatch(batches, cancellationToken));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(request, 1, "C:7:100:1");
        await clientSide.WriteAsync(request.WrittenMemory, CancellationToken.None);
        await clientSide.FlushAsync(CancellationToken.None);

        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var batch = await ReadOneFrameAsync(clientSide).WaitAsync(cts.Token);
        Assert.AreEqual(BrokerFrameKind.JournalBatch, batch.Kind);
        Assert.AreEqual(2, batch.Entries.Length);

        await cts.CancelAsync();
        await serveTask;
    }

    [TestMethod]
    public async Task ArmAndScan_WithDiagnostics_FiltersCatchUpEntries_ButStillShipsTerminalBatch()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        EnableDiagFilterSeams();
        try
        {
            var host = CreateHost(
                _ => new UsnJournalCursor(7UL, 0L),
                (_, _, _) => [],
                (_, cursor) => (new[] { DiagEntry(DiagOwnLogReference), DiagEntry(4242, "real.txt") },
                    new UsnJournalCursor(cursor.JournalId, cursor.NextUsn + 2)));

            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-scan-C");
            await clientSide.WriteAsync(request.WrittenMemory);
            await clientSide.FlushAsync();

            await host.ServeAsync(serverSide, CreateSectionWriter(), true, CancellationToken.None);
            await serverSide.DisposeAsync();

            var frames = ReadAllFrames(clientSide);
            var batches = frames.Where(frame => frame.Kind == BrokerFrameKind.JournalBatch).ToList();
            // The terminal catch-up batch always ships (the client's scan collector waits
            // on it), with the log entries removed.
            Assert.AreEqual(1, batches.Count);
            Assert.AreEqual(1, batches[0].Entries.Length);
            Assert.AreEqual("real.txt", batches[0].Entries[0].FileName);
        }
        finally
        {
            ResetDiagFilterSeams();
        }
    }

    [TestMethod]
    public async Task ArmAndScan_WithDiagnostics_LogOnlyCatchUpEntries_ShipsEmptyTerminalBatch()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        EnableDiagFilterSeams();
        try
        {
            var host = CreateHost(
                _ => new UsnJournalCursor(7UL, 0L),
                (_, _, _) => [],
                (_, cursor) => (new[] { DiagEntry(DiagOwnLogReference) },
                    new UsnJournalCursor(cursor.JournalId, cursor.NextUsn + 1)));

            var request = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteArmAndScan(request, "C:0:0:mftlib-scan-C");
            await clientSide.WriteAsync(request.WrittenMemory);
            await clientSide.FlushAsync();

            await host.ServeAsync(serverSide, CreateSectionWriter(), true, CancellationToken.None);
            await serverSide.DisposeAsync();

            var frames = ReadAllFrames(clientSide);
            var batches = frames.Where(frame => frame.Kind == BrokerFrameKind.JournalBatch).ToList();
            // The terminal catch-up batch must still ship even when all entries were filtered out,
            // producing an empty JournalBatch so the client's scan collector completes the drive.
            Assert.AreEqual(1, batches.Count);
            Assert.AreEqual(0, batches[0].Entries.Length);
        }
        finally
        {
            ResetDiagFilterSeams();
        }
    }
}
