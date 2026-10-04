using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Runs <see cref="JournalBrokerHost.ServeAsync" /> over an in-memory control pipe and
///     in-memory drive pipes, and speaks raw frames to it the way a client would. Drive pipes
///     are created on demand by name, so the default connector hands the host the host end and a
///     test reads the client end of the same name. Reads skip <see cref="BrokerFrameKind.Heartbeat" />
///     frames, as the client does, unless a test asks for them.
/// </summary>
internal sealed class HostChannelHarness : IAsyncDisposable
{
    /// <summary>Bounds every wait on the host, so a hang fails the test instead of wedging it.</summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    readonly ConcurrentDictionary<string, InMemoryPipePair> _pipes = new(StringComparer.Ordinal);
    readonly InMemoryPipePair _control = new();
    readonly Func<string, Stream, Stream>? _wrapDrivePipe;
    readonly Task? _connectionsReleased;
    uint _nextRequestId;
    int _nextPipe;

    /// <summary>Starts <see cref="JournalBrokerHost.ServeAsync" /> on <paramref name="host" /> over a fresh control pipe.</summary>
    /// <param name="host">The host under test.</param>
    /// <param name="blockSectionWriter">The session's section writer; null serves a watch-only session.</param>
    /// <param name="connectChannel">The drive-pipe connector; null connects the named in-memory pipe.</param>
    /// <param name="breakableControl">
    ///     Serve the control pipe through a <see cref="BrokenPipeStream" />, exposed as
    ///     <see cref="BreakableControl" />, so a test can make the host's control writes fail.
    /// </param>
    /// <param name="wrapDrivePipe">Wraps the host end the default connector hands the host, given the pipe's name.</param>
    /// <param name="connectionsReleased">
    ///     Holds every connection the default connector makes until it completes, so a test can
    ///     let the host start waiting for a connection before the connection exists.
    /// </param>
    public HostChannelHarness(JournalBrokerHost host, IBlockSectionWriter? blockSectionWriter = null,
        BrokerChannelConnector? connectChannel = null, bool breakableControl = false,
        Func<string, Stream, Stream>? wrapDrivePipe = null, Task? connectionsReleased = null)
    {
        _wrapDrivePipe = wrapDrivePipe;
        _connectionsReleased = connectionsReleased;
        BreakableControl = breakableControl ? new BrokenPipeStream(_control.Host) : null;
        Serve = host.ServeAsync(BreakableControl ?? _control.Host, connectChannel ?? ConnectInMemoryAsync,
            blockSectionWriter, CancellationToken.None);
    }

    public BrokenPipeStream? BreakableControl { get; }

    /// <summary>The host's session; completes when <see cref="JournalBrokerHost.ServeAsync" /> returns.</summary>
    public Task Serve { get; }

    public uint NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    public string NextPipeName(char drive) =>
        $"harness-{drive}-{Interlocked.Increment(ref _nextPipe)}";

    /// <summary>The connector the host uses by default: the host end of the named in-memory pipe.</summary>
    public async Task<Stream> ConnectInMemoryAsync(string pipeName, CancellationToken cancellationToken)
    {
        if (_connectionsReleased != null)
        {
            await _connectionsReleased.WaitAsync(cancellationToken);
        }

        Stream hostEnd = Pipe(pipeName).Host;
        return _wrapDrivePipe?.Invoke(pipeName, hostEnd) ?? hostEnd;
    }

    /// <summary>The client end of the named in-memory drive pipe.</summary>
    public Stream DrivePipe(string pipeName) => Pipe(pipeName).Client;

    InMemoryPipePair Pipe(string pipeName) => _pipes.GetOrAdd(pipeName, _ => new InMemoryPipePair());

    public Task SendControlAsync(Action<IBufferWriter<byte>> write) => WriteFrameAsync(_control.Client, write);

    public async Task<BrokerFrame> ReadControlAsync(bool includeHeartbeats = false)
    {
        return await ReadFrameAsync(_control.Client, includeHeartbeats) ??
               throw new AssertFailedException("The control pipe ended.");
    }

    /// <summary>Opens a drive channel and returns the client end of its pipe once ChannelOpened arrives.</summary>
    public async Task<Stream> OpenChannelAsync(char drive)
    {
        var requestId = NextRequestId();
        var pipeName = NextPipeName(drive);
        await SendControlAsync(writer => BrokerProtocol.WriteOpenChannel(writer, requestId, drive.ToString(), pipeName));
        var reply = await ReadControlAsync();
        Assert.AreEqual(BrokerFrameKind.ChannelOpened, reply.Kind, reply.Message);
        Assert.AreEqual(requestId, reply.RequestId);
        return DrivePipe(pipeName);
    }

    public async Task<Stream> OpenScanChannelAsync(char drive, string sectionName = "section",
        BrokerScanProfile profile = BrokerScanProfile.Full, IReadOnlyCollection<string>? keepFileNames = null)
    {
        var pipe = await OpenChannelAsync(drive);
        await WriteFrameAsync(pipe,
            writer => BrokerProtocol.WriteArmAndScan(writer, sectionName, profile, keepFileNames));
        return pipe;
    }

    public async Task<Stream> OpenWatchChannelAsync(char drive, UsnJournalCursor since)
    {
        var pipe = await OpenChannelAsync(drive);
        await WriteFrameAsync(pipe, writer => BrokerProtocol.WriteStartWatch(writer, since));
        return pipe;
    }

    /// <summary>The client closing its end of the control pipe.</summary>
    public ValueTask CloseControlAsync() => _control.Client.DisposeAsync();

    public static async Task WriteFrameAsync(Stream stream, Action<IBufferWriter<byte>> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        using var deadline = new CancellationTokenSource(HangGuard);
        await stream.WriteAsync(buffer.WrittenMemory, deadline.Token);
        await stream.FlushAsync(deadline.Token);
    }

    /// <summary>The next frame, or null once the host has closed its end.</summary>
    public static async Task<BrokerFrame?> ReadFrameAsync(Stream stream, bool includeHeartbeats = false)
    {
        while (true)
        {
            var frame = await ReadAnyFrameAsync(stream);
            if (includeHeartbeats || frame is not { Kind: BrokerFrameKind.Heartbeat })
            {
                return frame;
            }
        }
    }

    static async Task<BrokerFrame?> ReadAnyFrameAsync(Stream stream)
    {
        using var deadline = new CancellationTokenSource(HangGuard);
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, deadline.Token);
        if (read == 0)
        {
            return null;
        }

        Assert.AreEqual(header.Length, read, "The pipe ended inside a frame header.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frame = new byte[4 + length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(4), deadline.Token);
        return BrokerProtocol.ReadFrame(frame, out _);
    }

    /// <summary>Every frame until the host closes its end.</summary>
    public static async Task<List<BrokerFrame>> ReadToEndAsync(Stream stream, bool includeHeartbeats = false)
    {
        var frames = new List<BrokerFrame>();
        while (await ReadFrameAsync(stream, includeHeartbeats) is { } frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseControlAsync();
        try
        {
            await Serve.WaitAsync(HangGuard);
        }
        finally
        {
            if (BreakableControl != null)
            {
                await BreakableControl.DisposeAsync();
            }

            await _control.DisposeAsync();
            foreach (var pipe in _pipes.Values)
            {
                await pipe.DisposeAsync();
            }
        }
    }
}
