using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public partial class JournalBrokerScanSessionTests : BrokerBlockTestBase
{
    static readonly string[] BareDriveC = ["C"];
    static readonly string[] DriveC = ["C:\\"];
    static readonly string[] DriveD = ["D:\\"];
    static readonly string[] DrivesCAndD = ["C:\\", "D:\\"];

    [TestCleanup]
    public void Cleanup()
    {
        JournalBrokerClient.ResetToDefaults();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> OneBatchAsync(
        (UsnJournalEntry[] Entries, UsnJournalCursor Cursor) batch)
    {
        yield return batch;
        await Task.CompletedTask;
    }

    JournalBrokerClient MakeMinimalFakeClient(Stream pipe)
    {
        return new JournalBrokerClient(pipe, (letter, options) => ($"mftlib-null-{letter}", CreateBlock(options), NoOpDisposable.Instance));
    }

    // Runs a broker task that reads one ArmAndScan request and replies with a
    // minimal happy-path Cursor + ScanReady + JournalBatch sequence for one drive.
    static Task RespondToArmAndScanAsync(Stream serverSide, string drive)
    {
        return Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, drive, new UsnJournalCursor(7UL, 0L));
            BrokerProtocol.WriteScanReady(response, $"mftlib-null-{drive}", 0, 0, 0);
            BrokerProtocol.WriteJournalBatch(response, drive, new UsnJournalCursor(7UL, 0L),
                Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });
    }

    static async Task<BrokerFrame> ReadOneFrameAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        await stream.ReadExactlyAsync(frameBytes.AsMemory(4, totalLength));
        var frame = BrokerProtocol.ReadFrame(frameBytes, out _);
        if (frame.Kind == BrokerFrameKind.QueryVolumes)
        {
            await ReplyToVolumeQueryAsync(stream, frame);
            return await ReadOneFrameAsync(stream);
        }

        return frame;
    }

    // Invokes the private BrokerDied backing delegate directly, simulating broker
    // death without a live watch (Task 2 has no background reader to detect a real
    // EOF once parked; Task 3's live watch exercises the real detection path).
    static void RaiseBrokerDied(JournalBrokerClient client, string reason)
    {
        var field = typeof(JournalBrokerClient).GetField(
            nameof(JournalBrokerClient.BrokerDied), BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handler = (Action<string>?)field.GetValue(client);
        handler?.Invoke(reason);
    }

    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    // Wraps a stream and records whether it was disposed, so a test can assert the
    // session disposed its owned client without depending on the underlying stream's
    // own post-dispose exception semantics.
    sealed class DisposeTrackingStream(Stream inner) : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return inner.Read(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return inner.FlushAsync(cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];

        public void Report(T value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }

    [TestMethod]
    public async Task ReplaceWatchCursors_WarmSession_WatchesReplacedSetNotBaseline()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);

        var baselineCursor = new UsnJournalCursor(7UL, 100L);
        var session = await JournalBrokerScanSession.StartFromCursorsAsync(
            _ => Task.FromResult(client),
            new Dictionary<string, UsnJournalCursor> { ["C"] = baselineCursor },
            BrokerScanProfile.Full,
            cancellationToken: CancellationToken.None);

        Assert.AreEqual(baselineCursor, session.WatchCursors["C"]);

        var replacedCursor = new UsnJournalCursor(7UL, 888L);
        session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor> { ["C"] = replacedCursor });
        Assert.AreEqual(replacedCursor, session.WatchCursors["C"]);

        var watchFrameTask = ReadOneFrameAsync(serverSide);
        await session.StartWatchAsync();
        var watchFrame = await watchFrameTask;

        Assert.AreEqual(BrokerFrameKind.StartWatch, watchFrame.Kind);
        StringAssert.Contains(watchFrame.DrivesSpec, "C:7:888");

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task WatchCursors_ReflectsReplacement_AndReflectsRescanAfterwards()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var initialScanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await initialScanTask;

        Assert.AreEqual(new UsnJournalCursor(7UL, 0L), session.WatchCursors["C"]);

        // Replace cursors
        var customCursor = new UsnJournalCursor(7UL, 555L);
        session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor> { ["C"] = customCursor });
        Assert.AreEqual(customCursor, session.WatchCursors["C"]);

        // Rescan: rescan overwrites watch cursors with its advanced cursors
        var rescanAdvancedCursor = new UsnJournalCursor(7UL, 999L);
        var rescanTask = Task.Run(async () =>
        {
            await ReadOneFrameAsync(serverSide);
            var response = new ArrayBufferWriter<byte>();
            BrokerProtocol.WriteCursor(response, "C", new UsnJournalCursor(7UL, 900L));
            BrokerProtocol.WriteScanReady(response, "mftlib-null-C", 0, 0, 0);
            BrokerProtocol.WriteJournalBatch(response, "C", rescanAdvancedCursor, Array.Empty<UsnJournalEntry>());
            await serverSide.WriteAsync(response.WrittenMemory);
            await serverSide.FlushAsync();
        });

        await session.RescanAsync(CreateOptions(session.Profile));
        await rescanTask;

        // WatchCursors now reflects the rescan's advanced cursor
        Assert.AreEqual(rescanAdvancedCursor, session.WatchCursors["C"]);

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task WatchCursors_SafeToReadWhenDisposedOrFaulted()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC, CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var cursor = new UsnJournalCursor(7UL, 123L);
        session.ReplaceWatchCursors(new Dictionary<string, UsnJournalCursor> { ["C"] = cursor });

        // Check faulted read
        RaiseBrokerDied(client, "faulted");
        Assert.AreEqual(cursor, session.WatchCursors["C"]);

        // Check disposed read
        await session.DisposeAsync();
        Assert.AreEqual(cursor, session.WatchCursors["C"]);
    }
}
