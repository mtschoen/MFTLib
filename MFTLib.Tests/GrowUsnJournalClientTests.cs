using MFTLib.Tests.TestSupport;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class GrowUsnJournalClientTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task GrowUsnJournalAsync_Success_SendsRequestAndReturnsSettings()
    {
        var received = new List<(string Drive, long MaximumSize, long AllocationDelta)>();
        await using var broker = new InProcessBroker(CreateHost(growUsnJournal: (drive, maximumSize, allocationDelta) =>
        {
            received.Add((drive, maximumSize, allocationDelta));
            return new UsnJournalSettings { MaximumSize = maximumSize, AllocationDelta = allocationDelta };
        }));

        var settings = await broker.Process.GrowUsnJournalAsync('C', 0x08000000, 0x01000000, CancellationToken.None)
            .WaitAsync(HangGuard);

        Assert.AreEqual(0x08000000L, settings.MaximumSize);
        Assert.AreEqual(0x01000000L, settings.AllocationDelta);
        Assert.AreEqual(1, received.Count);
        Assert.AreEqual("C", received[0].Drive);
        Assert.AreEqual(0x08000000L, received[0].MaximumSize);
        Assert.AreEqual(0x01000000L, received[0].AllocationDelta);
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_ErrorFrame_ThrowsWithTheRefusalMessage()
    {
        await using var broker = new InProcessBroker(CreateHost(growUsnJournal: (_, _, _) =>
            throw new InvalidOperationException("Refusing to resize the USN journal to 32 bytes: the current maximum " +
                                                "is 64 bytes, and only growth is permitted.")));

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            broker.Process.GrowUsnJournalAsync('C', 32, 16, CancellationToken.None).WaitAsync(HangGuard));

        StringAssert.Contains(exception.Message, "only growth");
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_BrokerDisconnects_Throws()
    {
        await using var broker = new ScriptedBroker();
        var request = broker.Process.GrowUsnJournalAsync('C', 0x08000000, 0x01000000, CancellationToken.None);
        var sent = await broker.ReadRequestAsync();
        await broker.CloseControlAsync();

        var exception = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => request.WaitAsync(HangGuard));

        Assert.AreEqual(BrokerFrameKind.GrowUsnJournal, sent.Kind);
        Assert.AreEqual("C", sent.Drive);
        Assert.AreEqual(0x08000000L, sent.JournalMaximumSize);
        Assert.AreEqual(0x01000000L, sent.JournalAllocationDelta);
        Assert.IsNull(exception.DriveLetter);
        Assert.IsFalse(string.IsNullOrEmpty(exception.Message));
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_NonPositiveSizes_AreForwardedAndTheHostRefusalIsThrown()
    {
        // The client does not validate sizes (BrokerProcess.Control.cs, GrowUsnJournalAsync): the
        // volume behind the host does, and its refusal returns as an Error frame.
        var received = new List<(long MaximumSize, long AllocationDelta)>();
        await using var broker = new InProcessBroker(CreateHost(growUsnJournal: (_, maximumSize, allocationDelta) =>
        {
            received.Add((maximumSize, allocationDelta));
            throw new ArgumentException("positive sizes are required");
        }));

        var zero = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            broker.Process.GrowUsnJournalAsync('C', 0, 0x01000000, CancellationToken.None).WaitAsync(HangGuard));
        var negative = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            broker.Process.GrowUsnJournalAsync('C', 0x08000000, -1, CancellationToken.None).WaitAsync(HangGuard));

        StringAssert.Contains(zero.Message, "positive sizes are required");
        StringAssert.Contains(negative.Message, "positive sizes are required");
        CollectionAssert.AreEqual(new[] { (0L, 0x01000000L), (0x08000000L, -1L) }, received);
    }
}
