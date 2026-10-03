using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// A length prefix no frame can have is malformed data on either side: it is rejected before
// anything is allocated for it, instead of overflowing the frame size or exhausting memory.
[TestClass]
public class BrokerFrameLengthTests
{
    static readonly TimeSpan HangGuard = HostChannelHarness.HangGuard;

    // The cap's arithmetic (BrokerFrameStream.MaximumFrameLength): the largest legitimate frame is an
    // ArmAndScan keep list, and 32,639 maximum-length NTFS names (255 UTF-16 units) still fit.
    [TestMethod]
    public void MaximumFrameLength_HoldsAKeepListOfThirtyTwoThousandMaximumLengthNames()
    {
        var names = Enumerable.Range(0, 32_639).Select(_ => new string('n', 255)).ToArray();
        var buffer = new ArrayBufferWriter<byte>();

        BrokerProtocol.WriteArmAndScan(buffer, "section", BrokerScanProfile.DirectoryIndex, names);

        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.WrittenSpan);
        Assert.IsTrue(totalLength <= BrokerFrameStream.MaximumFrameLength,
            $"A keep list of 32,639 maximum-length names is {totalLength} bytes.");
        Assert.AreEqual(names.Length, BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _).KeepFileNames.Count);
    }

    [DataTestMethod]
    [DataRow(int.MaxValue)]
    [DataRow(BrokerFrameStream.MaximumFrameLength + 1)]
    public async Task Host_ControlFrameLengthBeyondMaximum_EndsSessionWithInvalidData(int length)
    {
        var harness = new HostChannelHarness(CreateHost());
        try
        {
            await harness.SendControlAsync(writer => WriteLengthPrefix(writer, length));

            var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => harness.Serve.WaitAsync(HangGuard));
            StringAssert.Contains(exception.Message, length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => await harness.DisposeAsync());
        }
    }

    [TestMethod]
    public async Task Host_DriveFirstRequestLengthBeyondMaximum_WritesErrorAndOtherRequestsContinue()
    {
        await using var harness = new HostChannelHarness(CreateHost());
        var pipe = await harness.OpenChannelAsync('C');

        await HostChannelHarness.WriteFrameAsync(pipe, writer => WriteLengthPrefix(writer, int.MaxValue));
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        StringAssert.Contains(frames[0].Message, "malformed");
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 8, "C"));
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

        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => pending.WaitAsync(HangGuard));
        Assert.IsNull(lost.DriveLetter);
        StringAssert.Contains(await ended.WaitAsync(HangGuard), "2147483647");
    }

    [TestMethod]
    public async Task Client_DriveFrameLengthBeyondMaximum_ScanFailsWithChannelLost()
    {
        await using var broker = new ScriptedBroker();
        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
            CancellationToken.None);
        await broker.AnswerQueryVolumeAsync(new NtfsVolumeInformation(1024 * 1000, 1024, 512, 4096, 100, 10));
        await using var pipe = await broker.AcceptChannelAsync();
        Assert.AreEqual(BrokerFrameKind.ArmAndScan, (await HostChannelHarness.ReadFrameAsync(pipe))?.Kind);

        await HostChannelHarness.WriteFrameAsync(pipe, writer => WriteLengthPrefix(writer, int.MaxValue));

        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard));
        Assert.AreEqual('C', lost.DriveLetter);
        Assert.IsInstanceOfType<InvalidDataException>(lost.InnerException);
    }

    static void WriteLengthPrefix(IBufferWriter<byte> writer, int length)
    {
        BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), length);
        writer.Advance(4);
    }

    static JournalBrokerHost CreateHost()
    {
        return new JournalBrokerHost(
            _ => new UsnJournalCursor(7, 1000),
            (_, _, _, _, _) => [],
            (_, since, _) => (Array.Empty<UsnJournalEntry>(), since),
            queryVolumeInfo: _ => new NtfsVolumeInformation(1024 * 1000, 1024, 512, 4096, 100, 10));
    }
}
