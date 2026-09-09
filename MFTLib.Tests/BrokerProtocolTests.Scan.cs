using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProtocolTests
{
    [TestMethod]
    public async Task PrepareDriveScan_EmitsExactlyFiveFieldsPerDrive()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        await using var server = serverSide;
        await using var client = new JournalBrokerClient(clientSide,
            (letter, creation) => ($"section-{letter}", BlockFile.Create(creation), new MemoryStream()));
        var response = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(response, "C", "Scan not needed for spec test");
        BrokerProtocol.WriteError(response, "D", "Scan not needed for spec test");
        await server.WriteAsync(response.WrittenMemory);
        await server.WriteAsync(response.WrittenMemory);
        var options = new BrokerScanOptions
        {
            Profile = BrokerScanProfile.DirectoryIndex,
            BlockTargets = new Dictionary<string, BlockScanTarget>
            {
                ["C"] = CreateScanTarget(),
                ["D"] = CreateScanTarget()
            }
        };

        var result = await client.ArmScanAndCatchUpAsync(["c:\\", "D:"], options);

        Assert.AreEqual(BrokerFrameKind.QueryVolumes, (await ReadScanFrameAsync(server)).Kind);
        Assert.AreEqual(0, result.BlockOutcomes.Count);
        foreach (var target in options.BlockTargets.Values)
        {
            Assert.IsFalse(File.Exists(target.Path));
        }

        var frame = await ReadScanFrameAsync(server);
        Assert.AreEqual(BrokerFrameKind.ArmAndScan, frame.Kind);
        Assert.AreEqual("C:0:0:section-C:1,D:0:0:section-D:1", frame.DrivesSpec);
    }

    static BlockScanTarget CreateScanTarget() =>
        new(Path.Combine(Path.GetTempPath(), $"protocol-target-{Guid.NewGuid():N}.bin"), 123, true);

    static async Task<BrokerFrame> ReadScanFrameAsync(Stream server)
    {
        var header = new byte[4];
        await server.ReadExactlyAsync(header);
        var contentLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[header.Length + contentLength];
        header.CopyTo(frameBytes.AsMemory());
        await server.ReadExactlyAsync(frameBytes.AsMemory(header.Length));
        var frame = BrokerProtocol.ReadFrame(frameBytes, out var consumed);
        Assert.AreEqual(frameBytes.Length, consumed);
        return frame;
    }
}
