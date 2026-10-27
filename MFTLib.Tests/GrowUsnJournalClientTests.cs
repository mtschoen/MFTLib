using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class GrowUsnJournalClientTests : BrokerBlockTestBase
{
    [TestMethod]
    public async Task GrowUsnJournalAsync_Success_SendsRequestAndReturnsSettings()
    {
        var received = new List<(DriveKey Drive, long MaximumSize, long AllocationDelta)>();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            GrowUsnJournal = (drive, maximumSize, allocationDelta) =>
            {
                received.Add((drive, maximumSize, allocationDelta));
                return new UsnJournalSettings { MaximumSize = maximumSize, AllocationDelta = allocationDelta };
            }
        }));

        var settings = await broker.Process.GrowUsnJournalAsync('C', new UsnJournalSettings { MaximumSize = 0x08000000, AllocationDelta = 0x01000000 }, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(0x08000000L, settings.MaximumSize);
        Assert.AreEqual(0x01000000L, settings.AllocationDelta);
        Assert.HasCount(1, received);
        Assert.AreEqual(new DriveKey('C'), received[0].Drive);
        Assert.AreEqual(0x08000000L, received[0].MaximumSize);
        Assert.AreEqual(0x01000000L, received[0].AllocationDelta);
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_DefaultDrive_ThrowsArgumentException()
    {
        await using var broker = new ScriptedBroker();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            broker.Process.GrowUsnJournalAsync(default, new UsnJournalSettings { MaximumSize = 0x08000000, AllocationDelta = 0x01000000 }, CancellationToken.None));
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_ErrorFrame_ThrowsWithTheRefusalMessage()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            GrowUsnJournal = (_, _, _) =>
                throw new InvalidOperationException("Refusing to resize the USN journal to 32 bytes: the current maximum " +
                                                    "is 64 bytes, and only growth is permitted.")
        }));

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            broker.Process.GrowUsnJournalAsync('C', new UsnJournalSettings { MaximumSize = 32, AllocationDelta = 16 }, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains("only growth", exception.Message);
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_BrokerDisconnects_Throws()
    {
        await using var broker = new ScriptedBroker();
        var request = broker.Process.GrowUsnJournalAsync('C', new UsnJournalSettings { MaximumSize = 0x08000000, AllocationDelta = 0x01000000 }, CancellationToken.None);
        var sent = await broker.ReadRequestAsync();
        await broker.CloseControlAsync();

        var exception = await Assert.ThrowsExactlyAsync<BrokerChannelLostException>(() => request.WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual(BrokerFrameKind.GrowUsnJournal, sent.Kind);
        Assert.AreEqual(new DriveKey('C'), sent.RequireDrive());
        Assert.AreEqual(0x08000000L, sent.JournalMaximumSize);
        Assert.AreEqual(0x01000000L, sent.JournalAllocationDelta);
        Assert.IsNull(exception.Drive);
        Assert.IsFalse(string.IsNullOrEmpty(exception.Message));
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_NonPositiveSizes_AreForwardedAndTheHostRefusalIsThrown()
    {
        // The client does not validate sizes (BrokerProcess.Control.cs, GrowUsnJournalAsync): the
        // volume behind the host does, and its refusal returns as an Error frame.
        var received = new List<(long MaximumSize, long AllocationDelta)>();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            GrowUsnJournal = (_, maximumSize, allocationDelta) =>
            {
                received.Add((maximumSize, allocationDelta));
                throw new ArgumentException("positive sizes are required");
            }
        }));

        var zero = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            broker.Process.GrowUsnJournalAsync('C', new UsnJournalSettings { MaximumSize = 0, AllocationDelta = 0x01000000 }, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));
        var negative = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            broker.Process.GrowUsnJournalAsync('C', new UsnJournalSettings { MaximumSize = 0x08000000, AllocationDelta = -1 }, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.Contains("positive sizes are required", zero.Message);
        Assert.Contains("positive sizes are required", negative.Message);
        Assert.AreSequenceEqual([(0L, 0x01000000L), (0x08000000L, -1L)], received);
    }

    [TestMethod]
    public async Task GrowUsnJournalAsync_OverCapRefusal_SurfacesTheCapMessageThroughBrokerSession()
    {
        const long requested = (8L * 1024 * 1024 * 1024) + 1;
        const long allowed = 8L * 1024 * 1024 * 1024;
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => ArmedCursor,
            QueryVolumeInformation = _ => VolumeInformation,
            GrowUsnJournal = (_, maximumSize, _) =>
                throw new ArgumentOutOfRangeException(
                    nameof(maximumSize),
                    maximumSize,
                    $"Requested USN journal maximum size of {maximumSize} bytes exceeds the allowed cap of {allowed} bytes.")
        }));
        var handle = broker.Handle;
        await using var session = BrokerTestHarness.CreateSession(_ => Task.FromResult(handle));
        var pendingGrowth = session.GrowUsnJournalAsync('C',
                new UsnJournalSettings { MaximumSize = requested, AllocationDelta = 0x100000 },
                CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => pendingGrowth);

        Assert.Contains(requested.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message);
        Assert.Contains(allowed.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message);
    }

    public TestContext TestContext { get; set; } = null!;
}
