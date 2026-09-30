using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class TestExtensionsStreamTests
{
    sealed class ForwardingStream(Stream inner) : DelegatingStream(inner)
    {
        public void ReleaseUnmanagedResources() => Dispose(false);
    }

    sealed class ControlledReadStream : MemoryStream
    {
        public TaskCompletionSource<int> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) => new(Result.Task);
    }

    [TestMethod]
    public async Task Forwarding_PreservesBytesAndNonSeekingContract()
    {
        var inner = new MemoryStream();
        var stream = new ForwardingStream(inner);
        try
        {
            Assert.IsTrue(stream.CanRead);
            Assert.IsTrue(stream.CanWrite);
            Assert.IsFalse(stream.CanSeek);
            AssertNonSeekingContract(stream);
            stream.Write(new byte[] { 1, 2 }, 0, 2);
            await stream.WriteAsync(new byte[] { 3, 4 }.AsMemory());
            stream.Flush();
            await stream.FlushAsync(CancellationToken.None);
            inner.Position = 0;
            var bytes = new byte[4];
            Assert.AreEqual(2, stream.Read(bytes, 0, 2));
            Assert.AreEqual(2, await stream.ReadAsync(bytes.AsMemory(2)));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, bytes);
            stream.ReleaseUnmanagedResources();
            Assert.IsTrue(inner.CanRead, "Dispose(false) leaves managed resources alone.");
            stream.Dispose();
            Assert.IsFalse(inner.CanRead);
            Assert.IsFalse(stream.CanRead);
            Assert.IsFalse(stream.CanWrite);
        }
        finally
        {
            stream.Dispose();
            inner.Dispose();
        }
    }

    static void AssertNonSeekingContract(Stream nonSeekingStream)
    {
        Assert.ThrowsException<NotSupportedException>(() => _ = nonSeekingStream.Length);
        Assert.ThrowsException<NotSupportedException>(() => _ = nonSeekingStream.Position);
        Assert.ThrowsException<NotSupportedException>(() => nonSeekingStream.Position = 1);
        Assert.ThrowsException<NotSupportedException>(() => nonSeekingStream.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsException<NotSupportedException>(() => nonSeekingStream.SetLength(1));
    }

    [TestMethod]
    public async Task Duplex_UsesDifferentReadAndWriteStreamsAndClosesBoth()
    {
        using var input = new MemoryStream(new byte[] { 1, 2 });
        using var output = new MemoryStream();
        var stream = new InMemoryDuplexStream(input, output);
        try
        {
            Assert.IsTrue(stream.CanWrite);
            var bytes = new byte[2];
            Assert.AreEqual(1, stream.Read(bytes, 0, 1));
            Assert.AreEqual(1, await stream.ReadAsync(bytes.AsMemory(1)));
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, bytes);
            stream.Write(bytes, 0, 1);
            await stream.WriteAsync(bytes.AsMemory(1));
            stream.Flush();
            await stream.FlushAsync(CancellationToken.None);
            CollectionAssert.AreEqual(bytes, output.ToArray());
            stream.Dispose();
            Assert.IsFalse(input.CanRead);
            Assert.IsFalse(output.CanWrite);
            AssertDisposedSyncOperations(stream, bytes);
            await AssertDisposedAsyncOperations(stream, bytes);
        }
        finally
        {
            stream.Dispose();
        }
    }

    static void AssertDisposedSyncOperations(Stream disposedStream, byte[] buffer)
    {
        Assert.ThrowsException<ObjectDisposedException>(() => disposedStream.Read(buffer, 0, 1));
        Assert.ThrowsException<ObjectDisposedException>(() => disposedStream.Write(buffer, 0, 1));
        Assert.ThrowsException<ObjectDisposedException>(disposedStream.Flush);
    }

    static async Task AssertDisposedAsyncOperations(Stream disposedStream, byte[] buffer)
    {
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => disposedStream.FlushAsync(CancellationToken.None));
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => disposedStream.ReadAsync(buffer.AsMemory()).AsTask());
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => disposedStream.WriteAsync(buffer.AsMemory()).AsTask());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PendingRead_TranslatesMisuseOnlyAfterClose(bool close)
    {
        using var input = new ControlledReadStream();
        using var output = new MemoryStream();
        var stream = new InMemoryDuplexStream(input, output);
        try
        {
            var read = stream.ReadAsync(new byte[1].AsMemory()).AsTask();
            if (close)
            {
                stream.Dispose();
            }

            var failure = new InvalidOperationException("reader completed");
            input.Result.SetException(failure);
            if (close)
            {
                await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => read);
            }
            else
            {
                Assert.AreSame(failure, await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => read));
            }
        }
        finally
        {
            if (!close)
            {
                stream.Dispose();
            }
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HeldWrite_ReleasesBytesOrCancelsWithoutWriting(bool cancel)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inner = new MemoryStream();
        using var stream = new HeldWriteStream(inner, () => release.Task);
        using var cancellation = new CancellationTokenSource();
        var write = stream.WriteAsync(new byte[] { 7 }.AsMemory(), cancellation.Token).AsTask();
        try
        {
            Assert.IsFalse(write.IsCompleted);
            Assert.AreEqual(0L, inner.Length);
            if (cancel)
            {
                cancellation.Cancel();
                try
                {
                    await write;
                    Assert.Fail("Expected cancellation");
                }
                catch (OperationCanceledException) { }

                Assert.AreEqual(0L, inner.Length);
            }
            else
            {
                release.SetResult();
                await write;
                CollectionAssert.AreEqual(new byte[] { 7 }, inner.ToArray());
            }
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task AsyncDisposal_ClosesClientBeforeJoiningHost()
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inner = new MemoryStream();
        var stream = new HostLifetimeStream(inner, exited.Task);
        var disposal = stream.DisposeAsync().AsTask();
        try
        {
            Assert.IsFalse(inner.CanRead);
            Assert.IsFalse(disposal.IsCompleted);
        }
        finally
        {
            exited.TrySetResult();
        }

        await disposal.WaitAsync(HostChannelHarness.HangGuard);
        await stream.DisposeAsync();
    }
}
