using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The control pipe's read loop: when it ends and how it reports a stream that ends mid-frame.
public partial class JournalBrokerHostTests
{
    [TestMethod]
    public async Task ServeAsync_TokenAlreadyCancelled_ReturnsImmediatelyWithoutReading()
    {
        var host = ScanHost();
        await using var control = new InMemoryPipePair();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var serve = host.ServeAsync(control.Host, (_, _) => throw new InvalidOperationException("unexpected connect"),
            null, cancellation.Token);

        await serve.WaitAsync(HostChannelHarness.HangGuard);
    }

    [TestMethod]
    public async Task ServeAsync_ClientClosesAfterOneRequest_ReturnsCleanlyOnEof()
    {
        var host = ScanHost(queryVolumeInfo: _ => ControlVolume);
        await using var harness = new HostChannelHarness(host);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 1, "C"));
        Assert.AreEqual(BrokerFrameKind.VolumeInfo, (await harness.ReadControlAsync()).Kind);
        await harness.CloseControlAsync(); // the host's next read hits clean EOF

        await harness.Serve.WaitAsync(HostChannelHarness.HangGuard);
    }

    // A length prefix claims a 10-byte frame; the pipe closes after three body bytes (EOF partway
    // through the body read) or after none (EOF exactly at the frame boundary).
    [DataTestMethod]
    [DataRow(3, DisplayName = "TruncatedFrameBody")]
    [DataRow(0, DisplayName = "HeaderOnlyThenEof")]
    public async Task ServeAsync_FrameCutShortByEof_ThrowsEndOfStreamException(int deliveredBodyBytes)
    {
        var host = ScanHost();
        var harness = new HostChannelHarness(host);

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10);
        await harness.SendControlAsync(writer =>
        {
            writer.Write(header);
            writer.Write(new byte[deliveredBodyBytes]);
        });
        await harness.CloseControlAsync();

        // The session's fault surfaces from its serve task, which disposing the harness awaits.
        await Assert.ThrowsExceptionAsync<EndOfStreamException>(async () => await harness.DisposeAsync());
    }
}
