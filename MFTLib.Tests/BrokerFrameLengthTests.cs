using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// A length prefix no frame can have is malformed data on either side: it is rejected before
// anything is allocated for it, instead of overflowing the frame size or exhausting memory.
[TestClass]
public class BrokerFrameLengthTests
{
    [TestMethod]
    public void ArmAndScan_FrameLengthIsKindSectionAndFlag()
    {
        const string sectionName = "section-C";
        var buffer = new ArrayBufferWriter<byte>();

        BrokerProtocol.WriteArmAndScan(buffer, BrokerFrame.ArmAndScan(sectionName, includeFreed: true));

        var expectedLength = 1 + 4 + (2 * sectionName.Length) + 1;
        Assert.AreEqual(expectedLength, BinaryPrimitives.ReadInt32LittleEndian(buffer.WrittenSpan));
        Assert.AreEqual(expectedLength + 4, buffer.WrittenCount);
    }

    [TestMethod]
    [DataRow(int.MaxValue)]
    [DataRow(BrokerFrameStream.MaximumFrameLength + 1)]
    public async Task Host_ControlFrameLengthBeyondMaximum_EndsSessionWithInvalidData(int length)
    {
        var harness = new HostChannelHarness(BrokerHostFactory.Create(new() { ScanVolume = (_, _, _, _, _, _) => [], ProcessorCount = null }));
        try
        {
            await harness.SendControlAsync(writer => WriteLengthPrefix(writer, length));

            var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => harness.Serve.WaitAsync(HangGuard, TestContext.CancellationToken));
            Assert.Contains(length.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message);
        }
        finally
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await harness.DisposeAsync());
        }
    }

    [TestMethod]
    public async Task Host_VolumeFirstRequestLengthBeyondMaximum_WritesErrorAndOtherRequestsContinue()
    {
        await using var harness = new HostChannelHarness(BrokerHostFactory.Create(new() { ScanVolume = (_, _, _, _, _, _) => [], ProcessorCount = null }));
        var pipe = await harness.OpenChannelAsync('C');

        await HostChannelHarness.WriteFrameAsync(pipe, writer => WriteLengthPrefix(writer, int.MaxValue));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.HasCount(1, frames);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        Assert.Contains("malformed", frames[0].Message!);
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, BrokerFrame.QueryVolume(8, 'C')));
        Assert.AreEqual(BrokerFrameKind.VolumeInfo, (await harness.ReadControlAsync()).Kind,
            "Only the channel that received the malformed frame ends.");
    }

    [TestMethod]
    public async Task Client_ControlFrameLengthBeyondMaximum_EndsProcessWithChannelLost()
    {
        await using var broker = new ScriptedBroker();
        var ended = broker.Process.Ended;
        var pending = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        await broker.ReadRequestAsync();

        await broker.WriteControlAsync(writer => WriteLengthPrefix(writer, int.MaxValue));

        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => pending.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.IsNull(lost.Volume);
        Assert.Contains("2147483647", await ended.WaitAsync(HangGuard, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Client_VolumeFrameLengthBeyondMaximum_ScanFailsWithChannelLost()
    {
        await using var broker = new ScriptedBroker();
        var scan = broker.Process.ScanVolumeAsync('C', TestBlockSections.Target(), new MftScanOptions(),
            null, CancellationToken.None);
        await broker.AnswerQueryVolumeAsync(new NtfsVolumeInformation(1024 * 1000, 1024));
        await using var pipe = await broker.AcceptChannelAsync();
        Assert.AreEqual(BrokerFrameKind.ArmAndScan, (await HostChannelHarness.ReadFrameAsync(pipe))?.Kind);

        await HostChannelHarness.WriteFrameAsync(pipe, writer => WriteLengthPrefix(writer, int.MaxValue));

        var lost = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard, TestContext.CancellationToken));
        Assert.AreEqual('C', lost.Volume);
        Assert.IsInstanceOfType<InvalidDataException>(lost.InnerException);
    }

    static void WriteLengthPrefix(IBufferWriter<byte> writer, int length)
    {
        BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), length);
        writer.Advance(4);
    }

    public TestContext TestContext { get; set; } = null!;
}
