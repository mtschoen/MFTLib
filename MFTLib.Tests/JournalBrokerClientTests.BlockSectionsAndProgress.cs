using System.Buffers;
using System.IO.Pipes;
using System.Runtime.Versioning;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class JournalBrokerClientTests
{
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_DiagEnvVarSet_AppendsDiagFlag()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", "1");
        try
        {
            string? capturedArgs = null;
            var launchBroker = new Func<string, bool>(args =>
            {
                capturedArgs = args;
                return false; // decline immediately; this test only cares about the args string
            });

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                JournalBrokerClient.SpawnAndConnectAsync(launchBroker));

            Assert.IsNotNull(capturedArgs);
            StringAssert.EndsWith(capturedArgs, "--diag");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MFTLIB_BROKER_DIAG", null);
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SpawnAndConnectAsync_EndToEnd_UsesRealPipeAndRealBlockSeams()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named memory-mapped files and named pipes require Windows.");
        }

        Task? brokerTask = null;

        var launchBroker = new Func<string, bool>(args =>
        {
            var parts = args.Split(' ');
            var pipeName = parts[Array.IndexOf(parts, "--pipe") + 1];
            brokerTask = Task.Run(async () =>
            {
                await using var pipe = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);

                // A minimal fake host: one drive, zero records, zero catch-up entries.
                // Write into the client-created named block section over a real pipe.
                var fakeHost = CreateHost(
                    _ => new UsnJournalCursor(7UL, 0L),
                    (_, _, _) => [],
                    (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));

                await fakeHost.ServeAsync(pipe, new RealBlockSectionWriter(), true, CancellationToken.None);
            });
            return true;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await JournalBrokerClient.SpawnAndConnectAsync(launchBroker, cts.Token);

        var result = await client.ArmScanAndCatchUpAsync(DriveC, CreateOptions(), cancellationToken: cts.Token);
        // The real section factory built this block, so the base class cleanup does not know
        // about it and client disposal does not reach it: a block returned from a scan is the
        // caller's. Scope it here so a failing assertion below still releases the mapping.
        using var block = result.BlockOutcomes["C"].Block;

        Assert.IsTrue(result.ArmedCursors.ContainsKey("C"));
        Assert.AreEqual(0, result.Errors.Count);

        await brokerTask!.WaitAsync(cts.Token);
    }

    // ---------------------------------------------------------------------------
    // Disposal & Streaming tests (Task 6)
    // ---------------------------------------------------------------------------

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_DisposesSection_AfterScanReady_WhileUnreadDriveStaysLive()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var trackerC = RegisterResource(new TrackingDisposable());
        var trackerD = RegisterResource(new TrackingDisposable());

        var client = new JournalBrokerClient(
            clientSide,
            (letter, options) => (letter == "C" ? "mmf-C" : "mmf-D", CreateBlock(options), letter == "C" ? trackerC : trackerD));

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // Read ArmAndScan

            // Drive C
            var responseC = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(responseC, "C", new UsnJournalCursor(7UL, 100L));
            BrokerProtocol.WriteScanReady(responseC, "mmf-C", 1, 100, 0);
            await serverSide.WriteAsync(responseC.WrittenMemory);
            await serverSide.FlushAsync();

            await trackerC.DisposedTask; // wait until client consumed ScanReady for C and disposed the map

            // At this point, C should be disposed, D should still be live
            Assert.IsTrue(trackerC.IsDisposed, "Drive C's map must be disposed after consumption");
            Assert.IsFalse(trackerD.IsDisposed, "Drive D's map must stay live while unread");

            // Complete C's catchup
            var catchupC = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteJournalBatch(catchupC, "C", new UsnJournalCursor(7UL, 110L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(catchupC.WrittenMemory);
            await serverSide.FlushAsync();

            // Drive D
            var responseD = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(responseD, "D", new UsnJournalCursor(8UL, 200L));
            BrokerProtocol.WriteScanReady(responseD, "mmf-D", 1, 100, 0);
            BrokerProtocol.WriteJournalBatch(responseD, "D", new UsnJournalCursor(8UL, 210L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(responseD.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var result = await client.ArmScanAndCatchUpAsync(["C", "D"], CreateOptions());

        await brokerTask;

        Assert.IsTrue(trackerC.IsDisposed);
        Assert.IsTrue(trackerD.IsDisposed);
        Assert.AreEqual(2, result.BlockOutcomes.Count);

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_ErrorFrame_DisposesSectionLifetimeImmediately()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var trackerD = RegisterResource(new TrackingDisposable());

        var client = new JournalBrokerClient(
            clientSide,
            (_, options) => ("mmf-D", CreateBlock(options), trackerD));

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // Read ArmAndScan
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteError(response, "D", "drive failed");
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var result = await client.ArmScanAndCatchUpAsync(DriveD,
            CreateOptions());
        await brokerTask;

        Assert.IsTrue(trackerD.IsDisposed, "Error frame must immediately dispose the failed drive's map");
        Assert.IsTrue(result.Errors.ContainsKey("D"));

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_Options_DispatchesProgressCallback()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var progress = new SyncProgress<BrokerScanProgress>();

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 100L));
            BrokerProtocol.WriteScanProgress(response,
                new BrokerScanProgress("C", 50, 1000, 100, 2000, TimeSpan.FromMilliseconds(50)));
            BrokerProtocol.WriteScanProgress(response,
                new BrokerScanProgress("C", 100, 2000, 100, 2000, TimeSpan.FromMilliseconds(100)));
            BrokerProtocol.WriteScanReady(response, "mftlib-progress-C", 100, 2000, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 200L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var client = MakeFakeClient(clientSide, "mftlib-progress-C");

        var options = new BrokerScanOptions
        {
            BlockTargets = CreateTargets(),
            Progress = progress
        };

        var result = await client.ArmScanAndCatchUpAsync(DriveC, options);
        await brokerTask;

        Assert.AreEqual(2, progress.Reports.Count);
        Assert.AreEqual(50, progress.Reports[0].RecordsProcessed);
        Assert.AreEqual(100, progress.Reports[1].RecordsProcessed);
        Assert.AreEqual("C", progress.Reports[0].DriveLetter);
        Assert.AreEqual(0, result.Errors.Count);

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_ScanProgressFrame_DoesNotCompleteDrive()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var progress = new SyncProgress<BrokerScanProgress>();

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 100L));
            BrokerProtocol.WriteScanProgress(response,
                new BrokerScanProgress("C", 10, 200, 100, 2000, TimeSpan.FromMilliseconds(10)));
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();

            var completeResponse = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteScanReady(completeResponse, "mftlib-scan-C", 100, 2000, 0);
            BrokerProtocol.WriteJournalBatch(completeResponse, "C", new UsnJournalCursor(7UL, 200L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(completeResponse.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var client = MakeFakeClient(clientSide, "mftlib-scan-C");

        var result = await client.ArmScanAndCatchUpAsync(DriveC, new BrokerScanOptions { BlockTargets = CreateTargets(), Progress = progress });
        await brokerTask;

        Assert.AreEqual(1, progress.Reports.Count);
        Assert.IsTrue(result.ArmedCursors.ContainsKey("C"));
        Assert.IsTrue(result.AdvancedCursors.ContainsKey("C"));

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task DemuxLoopAsync_ScanProgressFrame_IgnoredDuringLiveWatch()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // StartWatch
            var response = new ArrayBufferWriter<byte>();
            // Late progress frame arriving during live watch
            BrokerProtocol.WriteScanProgress(response,
                new BrokerScanProgress("C", 100, 2000, 100, 2000, TimeSpan.FromSeconds(1)));
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(7UL, 110L),
                [JournalEntryFactory.Create(1, 110, "live.txt")]);
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor> { ["C"] = new(7UL, 100L) });
        var batchSource = client.CreateBatchSource();

        var received = new List<(UsnJournalEntry[], UsnJournalCursor)>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var batch in batchSource("C:\\", default, cts.Token))
        {
            received.Add(batch);
            break;
        }

        await brokerTask;
        Assert.AreEqual(1, received.Count);
        Assert.AreEqual("live.txt", received[0].Item1[0].FileName);

        await client.DisposeAsync();
    }

    [TestMethod]
    public void NormalizeDriveLetter_ValidDriveFormats_ReturnsUppercaseLetter()
    {
        Assert.AreEqual("C", JournalBrokerClient.NormalizeDriveLetter("C"));
        Assert.AreEqual("C", JournalBrokerClient.NormalizeDriveLetter("c"));
        Assert.AreEqual("C", JournalBrokerClient.NormalizeDriveLetter("C:"));
        Assert.AreEqual("C", JournalBrokerClient.NormalizeDriveLetter("c:"));
        Assert.AreEqual("C", JournalBrokerClient.NormalizeDriveLetter(@"C:\"));
        Assert.AreEqual("C", JournalBrokerClient.NormalizeDriveLetter(@"c:\"));
        Assert.AreEqual("C", JournalBrokerClient.NormalizeDriveLetter("C:/"));
        Assert.AreEqual("C", JournalBrokerClient.NormalizeDriveLetter("c:/"));
        Assert.AreEqual("C", JournalBrokerClient.NormalizeDriveLetter(@"\\.\C:"));
        Assert.AreEqual("C", JournalBrokerClient.NormalizeDriveLetter(@"\\.\c:"));
        Assert.AreEqual("C", JournalBrokerClient.NormalizeDriveLetter(@"\\.\C"));
        Assert.AreEqual("C", JournalBrokerClient.NormalizeDriveLetter("  C:  "));
    }

    [TestMethod]
    public void NormalizeDriveLetter_InvalidInputs_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentNullException>(() => JournalBrokerClient.NormalizeDriveLetter(null!));
        Assert.ThrowsException<ArgumentException>(() => JournalBrokerClient.NormalizeDriveLetter(""));
        Assert.ThrowsException<ArgumentException>(() => JournalBrokerClient.NormalizeDriveLetter("   "));
        Assert.ThrowsException<ArgumentException>(() => JournalBrokerClient.NormalizeDriveLetter("C:garbage"));
        Assert.ThrowsException<ArgumentException>(() => JournalBrokerClient.NormalizeDriveLetter("C,D"));
        Assert.ThrowsException<ArgumentException>(() => JournalBrokerClient.NormalizeDriveLetter("1"));
        Assert.ThrowsException<ArgumentException>(() => JournalBrokerClient.NormalizeDriveLetter("1:"));
        Assert.ThrowsException<ArgumentException>(() => JournalBrokerClient.NormalizeDriveLetter("CD"));
        Assert.ThrowsException<ArgumentException>(() => JournalBrokerClient.NormalizeDriveLetter("$$"));
        Assert.ThrowsException<ArgumentException>(() => JournalBrokerClient.NormalizeDriveLetter(@"\\.\Volume{12345678-1234-1234-1234-123456789012}"));
        Assert.ThrowsException<ArgumentException>(() => JournalBrokerClient.NormalizeDriveLetter(@"C:\dir\file.txt"));
    }
}
