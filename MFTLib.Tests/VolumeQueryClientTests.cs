using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Client-side <see cref="JournalBrokerClient.QueryVolumesAsync" /> and the
///     <see cref="BrokerScanOptions.MmfCapacityPlanner" /> integration in
///     <see cref="JournalBrokerClient.ArmScanAndCatchUpAsync" /> (MFTLib#97).
/// </summary>
[TestClass]
public class VolumeQueryClientTests
{
    static readonly string[] DrivesCAndG = ["C", "G"];

    [TestMethod]
    public async Task QueryVolumesAsync_OneDriveSucceeds_OneErrors_ReturnsSuccessOnly()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // QueryVolumes request
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteVolumeInfo(response, "C", 8_000_000, 1024, 8_192_000_000);
            BrokerProtocol.WriteError(response, "G", "access denied");
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var result = await client.QueryVolumesAsync(DrivesCAndG);
        await brokerTask;

        Assert.IsTrue(result.Volumes.ContainsKey("C"));
        Assert.AreEqual(8_000_000L, result.Volumes["C"].MftRecordCount);
        Assert.AreEqual(1024U, result.Volumes["C"].BytesPerFileRecordSegment);
        Assert.AreEqual(8_192_000_000L, result.Volumes["C"].MftValidDataLength);
        Assert.AreEqual(0U, result.Volumes["C"].BytesPerSector);
        Assert.AreEqual(0U, result.Volumes["C"].BytesPerCluster);
        Assert.AreEqual(0L, result.Volumes["C"].TotalClusters);
        Assert.AreEqual(0L, result.Volumes["C"].FreeClusters);
        Assert.IsFalse(result.Volumes.ContainsKey("G"));
        Assert.AreEqual("access denied", result.Errors["G"]);
        Assert.IsFalse(result.Errors.ContainsKey("C"));

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task QueryVolumesAsync_BrokerDisconnectsMidExchange_ReportsRemainingDrivesInErrors()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // QueryVolumes request
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteVolumeInfo(response, "C", 8_000_000, 1024, 8_192_000_000);
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
            // Disconnect immediately before responding for drive G
            await serverSide.DisposeAsync();
        });

        var result = await client.QueryVolumesAsync(DrivesCAndG);
        await brokerTask;

        Assert.IsTrue(result.Volumes.ContainsKey("C"));
        Assert.AreEqual(8_000_000L, result.Volumes["C"].MftRecordCount);
        Assert.IsFalse(result.Volumes.ContainsKey("G"));
        Assert.IsTrue(result.Errors.ContainsKey("G"));
        Assert.IsTrue(result.Errors["G"].Contains("disconnected", StringComparison.OrdinalIgnoreCase));

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_CapacityPlannerSet_QueriesVolumesFirstThenSizesEachDriveMap()
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

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // QueryVolumes request

            var volumeResponse = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteVolumeInfo(volumeResponse, "C", 8_000_000, 1024, 8_192_000_000);
            BrokerProtocol.WriteError(volumeResponse, "G", "access denied");
            await serverSide.WriteAsync(volumeResponse.WrittenMemory);
            await serverSide.FlushAsync();

            await ReadOneFrameAsync(serverSide); // ArmAndScan request

            var scanResponse = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteError(scanResponse, "C", "unused");
            BrokerProtocol.WriteError(scanResponse, "G", "unused");
            await serverSide.WriteAsync(scanResponse.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await client.ArmScanAndCatchUpAsync(DrivesCAndG, new BrokerScanOptions
        {
            MmfCapacityPlanner = JournalBrokerClient.DefaultCapacityPlanner
        });
        await brokerTask;

        Assert.AreEqual(2, capturedCapacities.Count);
        Assert.AreEqual(
            JournalBrokerClient.DefaultCapacityPlanner("C", new NtfsVolumeInformation(8_192_000_000, 1024, 0, 0, 0, 0)),
            capturedCapacities["C"]);
        Assert.AreEqual(JournalBrokerClient.DefaultMmfCapacity, capturedCapacities["G"]); // planner(G, null)

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task ArmScanAndCatchUpAsync_CapacityPlannerReturnsNonPositive_ThrowsArgumentOutOfRangeException()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide); // QueryVolumes request
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteError(response, "C", "access denied");
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() =>
            client.ArmScanAndCatchUpAsync(["C"], new BrokerScanOptions
            {
                MmfCapacityPlanner = (_, _) => 0
            }));
        await brokerTask;

        await client.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // DefaultCapacityPlanner math
    // ---------------------------------------------------------------------------

    [TestMethod]
    public void DefaultCapacityPlanner_NullInfo_ReturnsDefaultMmfCapacity()
    {
        var capacity = JournalBrokerClient.DefaultCapacityPlanner("C", null);
        Assert.AreEqual(JournalBrokerClient.DefaultMmfCapacity, capacity);
    }

    [TestMethod]
    public void DefaultCapacityPlanner_ZeroRecordCount_ReturnsDefaultMmfCapacity()
    {
        // BytesPerFileRecordSegment = 0 makes MftRecordCount compute to 0 (the
        // divide-by-zero guard), which must be treated the same as "no count known".
        var info = new NtfsVolumeInformation(0, 0, 0, 0, 0, 0);
        var capacity = JournalBrokerClient.DefaultCapacityPlanner("C", info);
        Assert.AreEqual(JournalBrokerClient.DefaultMmfCapacity, capacity);
    }

    [TestMethod]
    public void DefaultCapacityPlanner_SmallVolume_FlooredAt256Mebibytes()
    {
        // 1,000 records * 480 bytes = 480,000 bytes, far under the 256 MiB floor.
        var info = new NtfsVolumeInformation(1_000 * 1024, 1024, 0, 0, 0, 0);
        var capacity = JournalBrokerClient.DefaultCapacityPlanner("C", info);
        Assert.AreEqual(256L * 1024 * 1024, capacity);
    }

    [TestMethod]
    public void DefaultCapacityPlanner_EightMillionRecords_RoundsUpTo256MebibyteMultiple()
    {
        // 8,000,000 * 384 * 1.25 = 3,840,000,000 bytes (exact: 384 * 5 / 4 = 480 is an
        // integer multiplier, so no floating-point rounding enters this computation).
        // 3,840,000,000 / 268,435,456 (256 MiB) = 14.305..., so it rounds up to the 15th
        // multiple: 15 * 268,435,456 = 4,026,531,840.
        var info = new NtfsVolumeInformation(8_000_000L * 1024, 1024, 0, 0, 0, 0);
        var capacity = JournalBrokerClient.DefaultCapacityPlanner("C", info);
        Assert.AreEqual(4_026_531_840L, capacity);
    }

    [TestMethod]
    public void DefaultCapacityPlanner_ExactMultipleBoundary_DoesNotRoundUpUnnecessarily()
    {
        // 8,388,608 records * 480 bytes = 4,026,531,840 bytes exactly, which is already
        // an exact multiple of 256 MiB (15 * 268,435,456). The ceiling-division must not
        // bump this up to the 16th multiple.
        var info = new NtfsVolumeInformation(8_388_608L * 1024, 1024, 0, 0, 0, 0);
        var capacity = JournalBrokerClient.DefaultCapacityPlanner("C", info);
        Assert.AreEqual(4_026_531_840L, capacity);
    }

    static JournalBrokerClient MakeMinimalFakeClient(Stream pipe)
    {
        return new JournalBrokerClient(
            pipe,
            new NullMmfReader(),
            (letter, _) => ($"mftlib-null-{letter}", NoOpDisposable.Instance));
    }

    // Every call site here reads a request frame only to discard it (the tests assert on
    // the client's return value instead), so this decodes-and-discards rather than
    // returning the frame - an unused Task<BrokerFrame> result would be dead weight.
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

    // Test double: IMmfReader that returns an empty array (for tests that do not need
    // real MMF data and inject an Error path instead).
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
