using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// A host session that faults reaches the client the way a broker process that exits does: its
// pipes close. The harness adds no fault surface of its own.
public partial class BrokerProcessTests
{
    [TestMethod]
    public async Task HostFault_WithPendingRequest_FailsRequestEndsProcessOnceAndDisposesQuietly()
    {
        var corrupt = new CorruptFrameWrite(corruptWriteNumber: 1);
        var broker = new InProcessBroker(CreateHost(), wrapClientStream: ControlOnly(corrupt));

        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() =>
            broker.Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard));

        Assert.IsNull(lost.DriveLetter);
        await broker.Process.Ended.WaitAsync(HangGuard);
        await broker.DisposeAsync();
    }

    [TestMethod]
    public async Task HostFault_WithOpenScanChannel_FailsScanWithChannelLost()
    {
        var scanning = new TestGate();
        var sourceCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Writes 1 and 2 are the scan's volume query and channel open; write 3 is corrupted.
        var corrupt = new CorruptFrameWrite(corruptWriteNumber: 3);
        var broker = new InProcessBroker(CreateHost(
                scanDrive: (_, _, _, _, cancellationToken) =>
                    WaitForCancellation(scanning, sourceCancelled, cancellationToken)),
            wrapClientStream: ControlOnly(corrupt));

        var scan = broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), new BrokerScanOptions(),
            CancellationToken.None);
        await scanning.Entered.WaitAsync(HangGuard);
        var query = broker.Process.QueryVolumeAsync('D', CancellationToken.None);

        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => scan.WaitAsync(HangGuard));
        Assert.AreEqual('C', lost.DriveLetter);
        await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => query.WaitAsync(HangGuard));
        await broker.Process.Ended.WaitAsync(HangGuard);
        AssertSectionReleased(broker.Sections.Single());
        await broker.DisposeAsync();
    }

    [TestMethod]
    public async Task HostFault_TestBodyThrowsInsideAwaitUsing_BodyExceptionEscapes()
    {
        var escaped = await Assert.ThrowsExceptionAsync<InvalidTimeZoneException>(async () =>
        {
            var corrupt = new CorruptFrameWrite(corruptWriteNumber: 1);
            await using var broker = new InProcessBroker(CreateHost(), wrapClientStream: ControlOnly(corrupt));
            var request = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
            try
            {
                await request.WaitAsync(HangGuard);
            }
            catch (BrokerChannelLostException)
            {
                throw new InvalidTimeZoneException("The test body failed after the host faulted.");
            }
        });

        Assert.AreEqual("The test body failed after the host faulted.", escaped.Message);
    }

    [TestMethod]
    public async Task HostFault_ExceptionMessageIsWrittenToDiagnosticsLog()
    {
        var lines = new List<string>();
        BrokerDiagnostics.Enable("client");
        BrokerDiagnostics.ReplaceWriterForTest(new BrokerDiagnosticsWriter(line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
        }));
        try
        {
            var corrupt = new CorruptFrameWrite(corruptWriteNumber: 1);
            await using (var broker = new InProcessBroker(CreateHost(), wrapClientStream: ControlOnly(corrupt)))
            {
                await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() =>
                    broker.Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard));
            }

            await BrokerDiagnostics.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            lock (lines)
            {
                Assert.IsTrue(lines.Any(line =>
                        line.Contains($":{BrokerDiagnostics.ControlChannel}]", StringComparison.Ordinal) &&
                        line.Contains("Unknown frame kind: 200", StringComparison.Ordinal)),
                    "The host session's failure must reach the log on the control channel.");
            }
        }
        finally
        {
            BrokerDiagnostics.ResetToDefaults();
        }
    }

    static Func<string, Stream, Stream> ControlOnly(CorruptFrameWrite corrupt)
    {
        return (name, stream) => name == "control" ? corrupt.Wrap(stream) : stream;
    }
}
