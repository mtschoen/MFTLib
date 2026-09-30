using System.Runtime.CompilerServices;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class TestExtensionsPipeTests
{
    [TestMethod]
    public async Task Listener_RejectsDuplicatesAndUnknownConnectionsAndCanBeReused()
    {
        var pipes = new InMemoryBrokerPipes(new BrokerTestHarnessOptions(), null);
        var first = pipes.Listen("drive");
        Assert.ThrowsException<IOException>(() => pipes.Listen("drive"));
        await Assert.ThrowsExceptionAsync<IOException>(() => pipes.ConnectAsync("missing", CancellationToken.None));
        var waiting = first.WaitForConnectionAsync(CancellationToken.None);
        await first.DisposeAsync();
        try
        {
            await waiting;
            Assert.Fail("Expected cancellation");
        }
        catch (OperationCanceledException) { }

        var replacement = pipes.Listen("drive");
        await first.DisposeAsync();
        await using var host = await pipes.ConnectAsync("drive", CancellationToken.None);
        await using var client = await replacement.WaitForConnectionAsync(CancellationToken.None);
        await host.WriteAsync(new byte[] { 9 }.AsMemory());
        var bytes = new byte[1];
        Assert.AreEqual(1, await client.ReadAsync(bytes.AsMemory()));
        Assert.AreEqual((byte)9, bytes[0]);
        await replacement.DisposeAsync();
        Assert.AreEqual(0, await host.ReadAsync(bytes.AsMemory()));
        pipes.CloseHostEnds();
    }

    [TestMethod]
    public async Task FailedConnection_PreservesExceptionAndListenerForRetry()
    {
        var failure = new IOException("injected connect failure");
        var fail = new StrongBox<bool>(true);
        var pipes = new InMemoryBrokerPipes(new BrokerTestHarnessOptions
        {
            FailConnection = name => name == "drive" && fail.Value ? failure : null
        }, null);
        await using var listener = pipes.Listen("drive");
        Assert.AreSame(failure, await Assert.ThrowsExceptionAsync<IOException>(() =>
            pipes.ConnectAsync("drive", CancellationToken.None)));
        fail.Value = false;
        await using var host = await pipes.ConnectAsync("drive", CancellationToken.None);
        await using var client = await listener.WaitForConnectionAsync(CancellationToken.None);
        pipes.CloseHostEnds();
        Assert.AreEqual(0, await client.ReadAsync(new byte[1].AsMemory()));
        await Assert.ThrowsExceptionAsync<IOException>(() => pipes.ConnectAsync("later", CancellationToken.None));
    }

    [TestMethod]
    public async Task ListenerClosedDuringPairCreation_RejectsConnectionAndClosesCreatedEnds()
    {
        var listenerBox = new StrongBox<BrokerPipeListener>();
        Stream? createdClient = null;
        Task? released = null;
        var pipes = new InMemoryBrokerPipes(new BrokerTestHarnessOptions(), (_, client) =>
        {
            createdClient = client;
            released = listenerBox.Value!.DisposeAsync().AsTask();
            return client;
        });
        var listener = pipes.Listen("drive");
        listenerBox.Value = listener;
        await using var ownedListener = listener;
        var failure = await Assert.ThrowsExceptionAsync<IOException>(() =>
            pipes.ConnectAsync("drive", CancellationToken.None));
        StringAssert.Contains(failure.Message, "closed before it connected");
        await released!;
        Assert.IsNotNull(createdClient);
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() =>
            createdClient.ReadAsync(new byte[1].AsMemory()).AsTask());
        pipes.CloseHostEnds();
    }

    [TestMethod]
    public void HostExit_ClosesExistingAndLateEndsAndIsIdempotent()
    {
        var ends = new HostPipeEnds();
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        Assert.IsFalse(ends.HasExited);
        ends.Track(first);
        Assert.IsTrue(first.CanRead);
        ends.CloseAll();
        Assert.IsTrue(ends.HasExited);
        Assert.IsFalse(first.CanRead);
        ends.Track(second);
        Assert.IsFalse(second.CanRead);
        ends.CloseAll();
    }
}
