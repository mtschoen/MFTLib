using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Drives a real <see cref="JournalBrokerClient" /> over an in-memory duplex stream pair, so a
///     watch-source test scripts broker frames by hand. The stream pair is the fake; nothing here
///     mocks the client.
/// </summary>
internal sealed class ScriptedWatchBrokerHarness : IAsyncDisposable
{
    readonly Stream _clientTransport;
    readonly Stream _server;
    readonly JournalBrokerClient _client;
    readonly CancellationTokenSource _hangGuard = new(TimeSpan.FromSeconds(10));

    public ScriptedWatchBrokerHarness()
    {
        var (client, server) = DuplexStream.CreatePair();
        _clientTransport = client;
        _server = server;
        _client = new JournalBrokerClient(client,
            (_, _) => throw new InvalidOperationException("Block creation is not expected."));
    }

    public JournalBrokerClient Client => _client;

    public CancellationToken CancellationToken => _hangGuard.Token;

    public int ConnectionCount { get; private set; }

    public Task<JournalBrokerClient> ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectionCount++;
        return Task.FromResult(_client);
    }

    /// <summary>
    ///     The arm epoch the client issued for one drive in the <c>StartWatch</c> frame just read,
    ///     so a scripted reply frame carries the epoch of the arm it is answering rather than a
    ///     literal that would pin the issue order instead of the behaviour under test.
    /// </summary>
    public uint ArmEpochForDrive(BrokerFrame startWatch, char driveLetter)
    {
        // Under the same hang guard as every other harness operation, so a call made after the
        // guard fired fails loudly instead of parsing a frame from a run that has already lost.
        _hangGuard.Token.ThrowIfCancellationRequested();
        return WatchSpecArmEpochs.ForDrive(startWatch, driveLetter.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     Tears the pipe out from under the client, which is what a broker that died mid-watch
    ///     leaves behind: the client's next frame write fails and its demux ends on the same
    ///     broken transport. Nothing may be read from or written to the harness afterwards.
    /// </summary>
    public ValueTask BreakTransportAsync()
    {
        return _clientTransport.DisposeAsync();
    }

    public async Task<BrokerFrame> ReadFrameAsync()
    {
        var header = new byte[4];
        await _server.ReadExactlyAsync(header, _hangGuard.Token);
        var frame = new byte[4 + BinaryPrimitives.ReadInt32LittleEndian(header)];
        header.CopyTo(frame, 0);
        await _server.ReadExactlyAsync(frame.AsMemory(4), _hangGuard.Token);
        return BrokerProtocol.ReadFrame(frame, out _);
    }

    public async Task WriteAsync(Action<ArrayBufferWriter<byte>> write)
    {
        var response = new ArrayBufferWriter<byte>();
        write(response);
        await _server.WriteAsync(response.WrittenMemory, _hangGuard.Token);
        await _server.FlushAsync(_hangGuard.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        _hangGuard.Dispose();
    }
}
