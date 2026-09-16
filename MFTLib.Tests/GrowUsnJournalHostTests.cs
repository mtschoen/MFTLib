using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Host-side handling of <see cref="BrokerFrameKind.GrowUsnJournal" />:
///     one <see cref="BrokerFrameKind.UsnJournalSettings" /> or <see cref="BrokerFrameKind.Error" />
///     reply per request, tagged <see cref="BrokerFrame.NoArmEpoch" />.
/// </summary>
[TestClass]
public class GrowUsnJournalHostTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task GrowUsnJournal_SeamReturnsSettings_EmitsUsnJournalSettings()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => default,
            (_, _, _) => Array.Empty<IReadOnlyList<MftRecord>>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            growUsnJournal: (_, maximumSize, allocationDelta) => new UsnJournalSettings
            {
                MaximumSize = maximumSize,
                AllocationDelta = allocationDelta
            });

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteGrowUsnJournal(request, "C", 0x08000000, 0x01000000);
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.UsnJournalSettings, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual(0x08000000L, frame.JournalMaximumSize);
        Assert.AreEqual(0x01000000L, frame.JournalAllocationDelta);
        Assert.AreEqual(BrokerFrame.NoArmEpoch, frame.ArmEpoch);

        await ShutdownAndAwaitAsync(clientSide, serveTask, cts);
    }

    [TestMethod]
    public async Task GrowUsnJournal_SeamThrows_EmitsErrorWithMessage()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => default,
            (_, _, _) => Array.Empty<IReadOnlyList<MftRecord>>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor),
            growUsnJournal: (_, _, _) => throw new InvalidOperationException(
                "Refusing to resize: only growth is permitted."));

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteGrowUsnJournal(request, "C", 0x08000000, 0x01000000);
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual(BrokerFrame.NoArmEpoch, frame.ArmEpoch);
        Assert.AreEqual("Refusing to resize: only growth is permitted.", frame.Message);

        await ShutdownAndAwaitAsync(clientSide, serveTask, cts);
    }

    [TestMethod]
    public async Task GrowUsnJournal_NoSeamConfigured_EmitsError()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var host = new JournalBrokerHost(
            _ => default,
            (_, _, _) => Array.Empty<IReadOnlyList<MftRecord>>(),
            (_, cursor) => (Array.Empty<UsnJournalEntry>(), cursor));
        // growUsnJournal omitted -> null

        var request = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteGrowUsnJournal(request, "C", 0x08000000, 0x01000000);
        await clientSide.WriteAsync(request.WrittenMemory);
        await clientSide.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveTask = host.ServeAsync(serverSide, CreateSectionWriter(), false, cts.Token);

        var frame = await ReadOneFrameAsync(clientSide);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual("C", frame.Drive);
        Assert.AreEqual("Broker has no journal grow source", frame.Message);

        await ShutdownAndAwaitAsync(clientSide, serveTask, cts);
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

    static async Task ShutdownAndAwaitAsync(Stream clientSide, Task serveTask, CancellationTokenSource cts)
    {
        var shutdown = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteShutdown(shutdown);
        await clientSide.WriteAsync(shutdown.WrittenMemory, cts.Token);
        await clientSide.FlushAsync(cts.Token);
        await serveTask;
    }
}
