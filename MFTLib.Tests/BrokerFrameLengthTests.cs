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
    static readonly TimeSpan HangGuard = HostChannelHarness.HangGuard;

    // The cap's arithmetic (BrokerFrameStream.MaximumFrameLength): the largest legitimate frame is an
    // ArmAndScan keep list. With the 107 bytes of kind, default section name, profile and count around it and 514
    // bytes per maximum-length NTFS name (255 UTF-16 units plus a 4-byte prefix), 32,640 names make
    // 107 + 514 * 32,640 = 16,777,067 bytes and fit; 32,641 names make 16,777,581 and do not.
    const int MaximumNameCount = 32_640;

    // The shape of a name NamedBlockSection builds: "mftlib-block-", a drive letter, a dash and 32 hex digits.
    static readonly string DefaultSectionName = "mftlib-block-C-" + new string('0', 32);

    [TestMethod]
    public void MaximumFrameLength_HoldsAKeepListOfThirtyTwoThousandSixHundredFortyMaximumLengthNames()
    {
        var names = MaximumLengthNames(MaximumNameCount);
        var sectionName = DefaultSectionName;
        var buffer = new ArrayBufferWriter<byte>();

        BrokerProtocol.WriteArmAndScan(buffer, sectionName, BrokerScanProfile.DirectoryIndex, names);

        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.WrittenSpan);
        Assert.AreEqual(16_777_067, totalLength);
        Assert.IsTrue(totalLength <= BrokerFrameStream.MaximumFrameLength);
        Assert.AreEqual(names.Length, BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _).KeepFileNames.Count);
    }

    [TestMethod]
    public void MaximumFrameLength_RefusesAKeepListOneNameOverTheBoundary()
    {
        var names = MaximumLengthNames(MaximumNameCount + 1);
        var sectionName = DefaultSectionName;
        var buffer = new ArrayBufferWriter<byte>();

        Assert.AreEqual(16_777_581L, BrokerProtocol.ArmAndScanFrameLength(sectionName, names));
        Assert.ThrowsException<InvalidOperationException>(() =>
            BrokerProtocol.WriteArmAndScan(buffer, sectionName, BrokerScanProfile.DirectoryIndex, names));
        Assert.AreEqual(0, buffer.WrittenCount, "No length prefix is emitted for a frame over the limit.");
    }

    static string[] MaximumLengthNames(int count)
    {
        var name = new string('n', 255);
        return Enumerable.Repeat(name, count).ToArray();
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
        await broker.AnswerQueryVolumeAsync(new NtfsVolumeInformation(1024 * 1000, 1024));
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
            new JournalBrokerHost.VolumeSources(
                _ => new UsnJournalCursor(7, 1000),
                (_, _, _, _, _) => [],
                (_, since, _) => (Array.Empty<UsnJournalEntry>(), since),
                QueryVolumeInformation: _ => new NtfsVolumeInformation(1024 * 1000, 1024)));
    }
}
