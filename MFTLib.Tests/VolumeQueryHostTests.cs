using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Host-side handling of <see cref="BrokerFrameKind.QueryVolume" /> (MFTLib#97): one
///     <see cref="BrokerFrameKind.VolumeInfo" /> or <see cref="BrokerFrameKind.Error" /> reply per
///     request, carrying the request's id, through <see cref="JournalBrokerHost.ServeAsync" />.
/// </summary>
[TestClass]
public class VolumeQueryHostTests
{
    [TestMethod]
    public async Task QueryVolume_TwoRequests_OneSeamThrows_EmitsVolumeInfoAndError()
    {
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => new UsnJournalCursor(7, 0),
            ScanVolume = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = volume => volume == 'C'
                ? new NtfsVolumeInformation(8_192_000_000L, 1024)
                : throw new InvalidOperationException("access denied"),
            ProcessorCount = null
        });
        await using var harness = new HostChannelHarness(host);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, BrokerFrame.QueryVolume(1, 'C')));
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, BrokerFrame.QueryVolume(2, 'G')));
        var replies = new[] { await harness.ReadControlAsync(), await harness.ReadControlAsync() };

        var volumeInfo = replies.Single(f => f.Kind == BrokerFrameKind.VolumeInfo);
        Assert.AreEqual(1u, volumeInfo.RequestId);
        Assert.AreEqual(1024U, volumeInfo.BytesPerFileRecordSegment);
        Assert.AreEqual(8_192_000_000L, volumeInfo.MftValidDataLength);

        var error = replies.Single(f => f.Kind == BrokerFrameKind.Error);
        Assert.AreEqual(2u, error.RequestId);
        Assert.AreEqual("access denied", error.Message);
    }

    [TestMethod]
    public async Task QueryVolume_UnsupportedOperation_EmitsError()
    {
        await using var harness = new HostChannelHarness(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => new UsnJournalCursor(7, 0),
            ScanVolume = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            ProcessorCount = null
        }));

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, BrokerFrame.QueryVolume(14, 'C')));
        var frame = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual(14u, frame.RequestId);
        Assert.AreEqual("Broker has no volume information source", frame.Message);
    }

    [TestMethod]
    public async Task QueryVolume_DoesNotEndSession_SubsequentArmAndScanStillWorks()
    {
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => new UsnJournalCursor(7, 0),
            ScanVolume = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = _ => new NtfsVolumeInformation(1024, 1024),
            ProcessorCount = null
        });
        using var blockWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, blockWriter);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, BrokerFrame.QueryVolume(5, 'C')));
        var volumeInfo = await harness.ReadControlAsync();
        var scanFrames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'));

        Assert.AreEqual(BrokerFrameKind.VolumeInfo, volumeInfo.Kind, "Expected a VolumeInfo frame from the query");
        Assert.AreEqual(5u, volumeInfo.RequestId);
        Assert.Contains(f => f.Kind == BrokerFrameKind.ScanReady, scanFrames,
            "Expected the scan that followed to still complete");
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, scanFrames[^1].Kind);
    }
}
