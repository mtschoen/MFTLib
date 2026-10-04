using System.Buffers;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A <see cref="BrokerProcess" /> whose host side is the test itself: the test reads each
///     control request and writes whatever frames it wants on the control pipe and on each drive
///     pipe, including orders and replies a real host never produces.
/// </summary>
internal sealed class ScriptedBroker : IAsyncDisposable
{
    readonly Stream _hostControl;

    public ScriptedBroker(TimeProvider? clock = null, Func<Stream, Stream>? wrapClientControl = null,
        Func<string, Stream, Stream>? wrapClientDrivePipe = null)
    {
        var (client, host) = new InMemoryPipePair();
        _hostControl = host;
        Pipes = new InMemoryBrokerPipes(new BrokerTestHarnessOptions(), wrapClientDrivePipe);
        Process = new BrokerProcess(wrapClientControl?.Invoke(client) ?? client, Pipes, Sections.Create,
            clock ?? TimeProvider.System);
    }

    public BrokerProcess Process { get; }

    public InMemoryBrokerPipes Pipes { get; }

    public TestBlockSections Sections { get; } = new();

    public async Task<BrokerFrame> ReadRequestAsync()
    {
        return await HostChannelHarness.ReadFrameAsync(_hostControl).WaitAsync(HostChannelHarness.HangGuard)
               ?? throw new AssertFailedException("The client closed its control pipe.");
    }

    public Task WriteControlAsync(Action<IBufferWriter<byte>> write) =>
        HostChannelHarness.WriteFrameAsync(_hostControl, write);

    /// <summary>Reads the next request, which must be a volume query, and answers it.</summary>
    public async Task AnswerQueryVolumeAsync(NtfsVolumeInformation volume)
    {
        var request = await ReadRequestAsync();
        Assert.AreEqual(BrokerFrameKind.QueryVolume, request.Kind);
        await WriteControlAsync(writer => BrokerProtocol.WriteVolumeInfo(writer, request.RequestId,
            volume.BytesPerFileRecordSegment, volume.MftValidDataLength));
    }

    /// <summary>Reads the next request, which must open a channel, connects it, and acknowledges it.</summary>
    public async Task<Stream> AcceptChannelAsync()
    {
        var request = await ReadRequestAsync();
        Assert.AreEqual(BrokerFrameKind.OpenChannel, request.Kind);
        var pipe = await Pipes.ConnectAsync(request.RequirePipeName(), CancellationToken.None);
        await WriteControlAsync(writer => BrokerProtocol.WriteChannelOpened(writer, request.RequestId));
        return pipe;
    }

    /// <summary>The host closing its end of the control pipe, as a broker process that exits does.</summary>
    public ValueTask CloseControlAsync() => _hostControl.DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Process.DisposeAsync().AsTask().WaitAsync(HostChannelHarness.HangGuard);
        }
        finally
        {
            await _hostControl.DisposeAsync().AsTask().WaitAsync(HostChannelHarness.HangGuard);
            Sections.Dispose();
        }
    }
}
