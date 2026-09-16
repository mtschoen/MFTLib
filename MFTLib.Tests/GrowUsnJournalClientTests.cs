using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Client-side <see cref="JournalBrokerClient.GrowUsnJournalAsync" />: request shape,
///     success reply, refusal as a thrown exception, and disconnect mid-exchange.
/// </summary>
[TestClass]
public class GrowUsnJournalClientTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task GrowUsnJournalAsync_Success_SendsRequestAndReturnsSettings()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var brokerTask = Task.Run(async () =>
        {
            var request = await ReadOneFrameAsync(serverSide);
            Assert.AreEqual(BrokerFrameKind.GrowUsnJournal, request.Kind);
            Assert.AreEqual("C", request.Drive);
            Assert.AreEqual(0x08000000L, request.JournalMaximumSize);
            Assert.AreEqual(0x01000000L, request.JournalAllocationDelta);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteUsnJournalSettings(response, "C", 0x08000000, 0x01000000);
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var settings = await client.GrowUsnJournalAsync('C', 0x08000000, 0x01000000);
        await brokerTask;

        Assert.AreEqual(0x08000000L, settings.MaximumSize);
        Assert.AreEqual(0x01000000L, settings.AllocationDelta);

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_ErrorFrame_ThrowsWithTheRefusalMessage()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteError(response, "C", BrokerFrame.NoArmEpoch,
                "Refusing to resize the USN journal to 32 bytes: the current maximum is 64 bytes, " +
                "and only growth is permitted.");
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            client.GrowUsnJournalAsync('C', 32, 16));
        await brokerTask;

        StringAssert.Contains(exception.Message, "only growth");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_BrokerDisconnects_Throws()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var brokerTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide);
            await serverSide.DisposeAsync(); // disconnect before answering
        });

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            client.GrowUsnJournalAsync('C', 0x08000000, 0x01000000));
        await brokerTask;

        StringAssert.Contains(exception.Message, "disconnected");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_NonPositiveSizes_ThrowBeforeAnyFrameIsWritten()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        // The validation throws synchronously, before the exchange starts, so nothing is
        // written for the broker side to read (which is why serverSide is never touched).
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            client.GrowUsnJournalAsync('C', 0, 0x01000000));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            client.GrowUsnJournalAsync('C', 0x08000000, -1));

        await client.DisposeAsync();
        await serverSide.DisposeAsync();
    }

    JournalBrokerClient MakeMinimalFakeClient(Stream pipe)
    {
        return new JournalBrokerClient(pipe, (letter, options) => ($"mftlib-null-{letter}", CreateBlock(options), NoOpDisposable.Instance));
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

    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
