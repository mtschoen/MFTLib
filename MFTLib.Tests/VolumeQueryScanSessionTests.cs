using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Confirms <see cref="JournalBrokerScanSession.RescanAsync(BrokerScanOptions,CancellationToken)" />
///     forwards <see cref="BrokerScanOptions.MmfCapacityPlanner" /> unchanged through to
///     <see cref="JournalBrokerClient.ArmScanAndCatchUpAsync" /> on the same broker
///     (MFTLib#97) - the same code path <see cref="JournalBrokerScanSession.StartAsync(Func{CancellationToken,Task{JournalBrokerClient}},IReadOnlyList{string},BrokerScanOptions?,CancellationToken)" />
///     already uses, so no session-level wiring is needed for either.
/// </summary>
[TestClass]
public class VolumeQueryScanSessionTests
{
    static readonly string[] DriveC = ["C"];

    [TestMethod]
    public async Task RescanAsync_CapacityPlannerSet_QueriesVolumesOnTheSameBroker_NoSecondBrokerOrUac()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var capturedCapacities = new Dictionary<string, long>();

        var client = new JournalBrokerClient(
            clientSide,
            new NullMmfReader(),
            (letter, capacity) =>
            {
                capturedCapacities[letter] = capacity;
                return ($"mftlib-null-{letter}", NoOpDisposable.Instance);
            });

        // Initial start: a plain scan, no planner - establishes the parked session.
        var startTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(1UL, 0L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(1UL, 0L), Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await startTask;

        capturedCapacities.Clear(); // only the rescan's capacity matters to this test

        // Rescan with a planner: the broker must see QueryVolumes before ArmAndScan, still
        // on the same connection (no second broker, no second UAC).
        var rescanTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // QueryVolumes
            var volumeResponse = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteVolumeInfo(volumeResponse, "C", 8_000_000, 1024, 8_192_000_000);
            await serverSide.WriteAsync(volumeResponse.WrittenMemory);
            await serverSide.FlushAsync();

            await ReadOneFrameAsync(serverSide); // ArmAndScan
            var scanResponse = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(scanResponse, "C", new UsnJournalCursor(1UL, 0L));
            BrokerProtocol.WriteScanReady(scanResponse, "mftlib-null-C", 0, 0);
            BrokerProtocol.WriteJournalBatch(scanResponse, "C", new UsnJournalCursor(1UL, 0L), Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(scanResponse.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await session.RescanAsync(new BrokerScanOptions
        {
            MmfCapacityPlanner = JournalBrokerClient.DefaultCapacityPlanner
        });
        await rescanTask;

        Assert.AreEqual(1, capturedCapacities.Count);
        Assert.AreEqual(
            JournalBrokerClient.DefaultCapacityPlanner("C", new NtfsVolumeInformation(8_192_000_000, 1024, 0, 0, 0, 0)),
            capturedCapacities["C"]);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task RescanAsync_CancelledDuringQueryVolumes_DisposesClientAndSession()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();

        var client = new JournalBrokerClient(
            clientSide,
            new NullMmfReader(),
            (letter, _) => ($"mftlib-null-{letter}", NoOpDisposable.Instance));

        // Initial start: plain scan to establish a parked session.
        var startTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // ArmAndScan
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(1UL, 0L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", new UsnJournalCursor(1UL, 0L), Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var session = await JournalBrokerScanSession.StartAsync(
            _ => Task.FromResult(client), DriveC, BrokerScanProfile.Full, cancellationToken: CancellationToken.None);
        await startTask;

        // Not a `using var`: the token's cancel callback is passed to serverTask below,
        // so it is disposed explicitly at the end instead - safe because that Dispose()
        // runs only after serverTask has finished (awaited below).
        var cts = new CancellationTokenSource();
        Action cancel = cts.Cancel;

        var serverTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // QueryVolumes received
            cancel();
            // Hold the response - do not reply yet
        });

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            session.RescanAsync(new BrokerScanOptions
            {
                MmfCapacityPlanner = JournalBrokerClient.DefaultCapacityPlanner
            }, cts.Token));

        await serverTask;

        // The session must be disposed so stale responses cannot corrupt subsequent operations
        Assert.AreEqual(JournalBrokerSessionState.Disposed, session.State);
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() =>
            session.RescanAsync(CancellationToken.None));

        cts.Dispose();
    }

    // Both call sites read a request frame only to discard it (the assertions are on
    // capturedCapacities instead), so this decodes-and-discards rather than returning the
    // frame - an unused Task<BrokerFrame> result would be dead weight.
    static async Task ReadOneFrameAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        await stream.ReadExactlyAsync(frameBytes.AsMemory(4, totalLength));
        BrokerProtocol.ReadFrame(frameBytes, out _);
    }

    // Test double: IMmfReader that returns an empty array (no test here reads real MMF data).
    sealed class NullMmfReader : IMmfReader
    {
        public ScanRecord[] Read(string mmfName, long byteLength)
        {
            return Array.Empty<ScanRecord>();
        }
    }

    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
