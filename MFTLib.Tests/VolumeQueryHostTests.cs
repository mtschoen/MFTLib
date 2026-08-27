using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Host-side handling of <see cref="BrokerFrameKind.QueryVolumes" /> (MFTLib#97):
///     <see cref="JournalBrokerHost.HandleQueryVolumesAsync" /> via <see cref="JournalBrokerHost.ServeAsync" />.
/// </summary>
[TestClass]
public class VolumeQueryHostTests
{
    [TestMethod]
    public async Task QueryVolumes_TwoDrives_OneSeamThrows_EmitsVolumeInfoAndError()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => default,
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            queryVolumeInfo: drive => drive == "C"
                ? new NtfsVolumeInformation(8_192_000_000L, 1024, 512, 4096, 1_000_000, 500_000)
                : throw new InvalidOperationException("access denied"));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteQueryVolumes(request, "C:0:0,G:0:0");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory);
        await clientSide.FlushAsync();

        await host.ServeAsync(serverSide, new RecordingMmfWriter(), false, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        var volumeInfo = frames.Single(f => f.Kind == BrokerFrameKind.VolumeInfo);
        Assert.AreEqual("C", volumeInfo.Drive);
        Assert.AreEqual(8_192_000_000L / 1024, volumeInfo.RecordCount);
        Assert.AreEqual(1024U, volumeInfo.BytesPerFileRecordSegment);
        Assert.AreEqual(8_192_000_000L, volumeInfo.MftValidDataLength);

        var error = frames.Single(f => f.Kind == BrokerFrameKind.Error);
        Assert.AreEqual("G", error.Drive);
        Assert.AreEqual("access denied", error.Message);
    }

    [TestMethod]
    public async Task QueryVolumes_NoSeamConfigured_EmitsErrorPerDrive()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => default,
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));
        // queryVolumeInfo omitted -> null

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteQueryVolumes(request, "C:0:0");
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, new RecordingMmfWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("Broker has no volume information source", frame.Message);

        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);
        await serveTask;
    }

    [TestMethod]
    public async Task QueryVolumes_DoesNotEndSession_SubsequentArmAndScanStillWorks()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => new UsnJournalCursor(7UL, 0L),
            _ => Array.Empty<ScanRecord>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            queryVolumeInfo: _ => new NtfsVolumeInformation(1024, 1024, 512, 4096, 1, 1));

        var queryRequest = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteQueryVolumes(queryRequest, "C:0:0");
        await clientSide.WriteAsync(queryRequest.WrittenMemory);
        await clientSide.FlushAsync();

        var scanRequest = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(scanRequest, "C:0:0:mftlib-volumequery-scan-C");
        await clientSide.WriteAsync(scanRequest.WrittenMemory);
        await clientSide.FlushAsync();

        // oneShot: true ends the session after the ArmAndScan that follows the query.
        await host.ServeAsync(serverSide, new RecordingMmfWriter(), true, CancellationToken.None);
        await serverSide.DisposeAsync();

        var frames = ReadAllFrames(clientSide);
        Assert.IsTrue(frames.Any(f => f.Kind == BrokerFrameKind.VolumeInfo), "Expected a VolumeInfo frame from the query");
        Assert.IsTrue(frames.Any(f => f.Kind == BrokerFrameKind.ScanReady), "Expected the ArmAndScan that followed to still complete");
        Assert.IsTrue(frames.Any(f => f.Kind == BrokerFrameKind.JournalBatch));

        var volumeInfoIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.VolumeInfo);
        var scanReadyIndex = frames.FindIndex(f => f.Kind == BrokerFrameKind.ScanReady);
        Assert.IsTrue(volumeInfoIndex < scanReadyIndex, "The query's response must precede the scan's");
    }

    static async Task<BrokerFrame> ReadOneFrameAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        await stream.ReadExactlyAsync(frameBytes.AsMemory(4, totalLength));
        return BrokerProtocol.ReadFrame(frameBytes, out _);
    }

    static List<BrokerFrame> ReadAllFrames(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var frames = new List<BrokerFrame>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var frame = BrokerProtocol.ReadFrame(bytes.AsSpan(offset), out var consumed);
            frames.Add(frame);
            offset += consumed;
        }

        return frames;
    }
}
