using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class UsnJournalSyntheticTests
{
    [DataTestMethod]
    [DataRow(true, 1)]
    [DataRow(true, 2)]
    [DataRow(false, 1)]
    [DataRow(false, 2)]
    public async Task EndWatch_IdleNativeRead_Acknowledges(bool cancelBeforeIssue, int readNumber)
    {
        var pipe = await IdleUsnPipe.CreateAsync(readNumber);
        try
        {
            FileUtilities._getVolumeHandle = pipe.BorrowHandle;
            QueueSuccess(BuildQueryBuffer(journalId: 7, nextUsn: 200));
            var cancellationAttempted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var nativeCancel = MFTLibNative._cancelUsnJournalWatch;
            MFTLibNative._cancelUsnJournalWatch = handle =>
            {
                var cancelled = nativeCancel(handle);
                cancellationAttempted.TrySetResult(cancelled);
                return cancelled;
            };

            var (client, server) = DuplexStream.CreatePair();
            await using var clientLifetime = client;
            await using var serverLifetime = server;
            using var writer = new RecordingBlockSectionWriter();
            using var lifetime = new CancellationTokenSource();
            var serve = JournalBrokerHost.CreateDefault().ServeAsync(server, writer, false, lifetime.Token);
            try
            {
                await WriteCancellationFrameAsync(client,
                    buffer => BrokerProtocol.WriteStartWatch(buffer, 1, "C:7:200:1"));
                Assert.AreEqual(BrokerFrameKind.CaughtUp,
                    (await ReadCancellationFrameAsync(client)).Kind);
                if (readNumber == 2)
                {
                    await pipe.SendEmptyBatchAsync(201);
                }
                await IdleUsnPipe.AwaitSignalAsync(pipe.BeforeIssue);
                if (!cancelBeforeIssue)
                {
                    pipe.ContinueIssue.Set();
                    await IdleUsnPipe.AwaitSignalAsync(pipe.Issued);
                }

                await WriteCancellationFrameAsync(client, writer => BrokerProtocol.WriteEndWatch(writer, 1));
                var foundRequest = await cancellationAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (cancelBeforeIssue)
                {
                    Assert.IsFalse(foundRequest, "The regression must cancel before a request exists.");
                }
                pipe.ContinueIssue.Set();
                Assert.AreEqual(BrokerFrameKind.EndWatchAck,
                    (await ReadCancellationFrameAsync(client)).Kind);
                await WriteCancellationFrameAsync(client, BrokerProtocol.WriteShutdown);
                await serve.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                pipe.UnblockForCleanup();
                await lifetime.CancelAsync();
                await serve.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            FileUtilities.ResetToDefaults();
            pipe.Dispose();
        }
    }

    [DataTestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    public async Task Watch_IdleNativeRead_CancellationCompletes(bool withCursor, bool beforeIssue)
    {
        var pipe = await IdleUsnPipe.CreateAsync(1);
        try
        {
            FileUtilities._getVolumeHandle = pipe.BorrowHandle;
            using var volume = MftVolume.Open("C");
            using var cancellation = new CancellationTokenSource();
            var watch = ConsumeIdleWatchAsync(volume, withCursor, cancellation.Token);
            try
            {
                await IdleUsnPipe.AwaitSignalAsync(pipe.BeforeIssue);
                if (!beforeIssue)
                {
                    pipe.ContinueIssue.Set();
                    await IdleUsnPipe.AwaitSignalAsync(pipe.Issued);
                }
                await cancellation.CancelAsync();
                pipe.ContinueIssue.Set();
                await watch.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                pipe.UnblockForCleanup();
                await cancellation.CancelAsync();
                try
                {
                    await watch.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (InvalidOperationException) when (cancellation.IsCancellationRequested)
                {
                    // Closing the test pipe can fault the read during failed-test teardown.
                }
            }
        }
        finally
        {
            FileUtilities.ResetToDefaults();
            pipe.Dispose();
        }
    }

    static async Task ConsumeIdleWatchAsync(MftVolume volume, bool withCursor, CancellationToken token)
    {
        try
        {
            if (withCursor)
            {
                await foreach (var batch in volume.WatchUsnJournalWithCursor(Cursor, token))
                {
                    Assert.Fail($"Unexpected idle batch with {batch.Entries.Length} entries.");
                }
            }
            else
            {
                await foreach (var batch in volume.WatchUsnJournal(Cursor, token))
                {
                    Assert.Fail($"Unexpected idle batch with {batch.Length} entries.");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    static async Task WriteCancellationFrameAsync(Stream stream, Action<IBufferWriter<byte>> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.WriteAsync(buffer.WrittenMemory, deadline.Token);
        await stream.FlushAsync(deadline.Token);
    }

    static async Task<BrokerFrame> ReadCancellationFrameAsync(Stream stream)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, deadline.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frame = new byte[4 + length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(4), deadline.Token);
        return BrokerProtocol.ReadFrame(frame, out _);
    }
}
