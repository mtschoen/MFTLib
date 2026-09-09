using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Client-side <see cref="JournalBrokerClient.QueryVolumesAsync" /> responses and errors.
/// </summary>
[TestClass]
public class VolumeQueryClientTests : BrokerBlockTestBase
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

    JournalBrokerClient MakeMinimalFakeClient(Stream pipe)
    {
        return new JournalBrokerClient(pipe, (letter, options) => ($"mftlib-null-{letter}", CreateBlock(options), NoOpDisposable.Instance));
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

    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
