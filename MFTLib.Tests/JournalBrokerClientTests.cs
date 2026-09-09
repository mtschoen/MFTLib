using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public partial class JournalBrokerClientTests : BrokerBlockTestBase
{
    static readonly string[] DriveC = ["C:\\"];
    static readonly string[] DriveD = ["D:\\"];
    static readonly string[] KeepFileNamesGit = [".git"];

    // ---------------------------------------------------------------------------
    // Remaining edge cases: truncated frames, no-op stop, write failures, timeout
    // forcing, duplicate start guard, clean channel completion, broker-death via a
    // real protocol error, and the real SpawnAndConnectAsync/CreateRealDriveBlockSection path.
    // ---------------------------------------------------------------------------

    [TestCleanup]
    public void Cleanup()
    {
        JournalBrokerClient.ResetToDefaults();
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    // Client with an ordinary block file and a predictable section name.
    JournalBrokerClient MakeFakeClient(
        Stream pipe, string sectionName)
    {
        return new JournalBrokerClient(
            pipe,
            (_, options) => (sectionName, CreateBlock(options), NoOpDisposable.Instance));
    }

    // Client with ordinary temporary block files for protocol tests.
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
        var frame = BrokerProtocol.ReadFrame(frameBytes, out _);
        if (frame.Kind == BrokerFrameKind.QueryVolumes)
        {
            await ReplyToVolumeQueryAsync(stream, frame);
            return await ReadOneFrameAsync(stream);
        }

        return frame;
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

    sealed class TrackingDisposable : IDisposable
    {
        readonly TaskCompletionSource _disposedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed { get; private set; }
        public Task DisposedTask => _disposedTcs.Task;

        public void Dispose()
        {
            IsDisposed = true;
            _disposedTcs.TrySetResult();
        }
    }

    // Disposable no-op lifetime handle for tests.
    sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
