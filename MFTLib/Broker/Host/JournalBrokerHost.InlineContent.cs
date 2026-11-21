using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using MFTLib.Index;

namespace MFTLib;

/// <summary>Serves one chunk of inline content reads on a volume channel.</summary>
sealed partial class JournalBrokerHost
{
    // The processing step an inline content read publishes, which a Stalled message names.
    const string InlineContentStep = "inline content read";

    // A batch frame closes once its answers hold this many payload bytes, far below the frame limit; an answer is at
    // most one MFT record, so a frame stays under the limit whatever fills it.
    internal const int InlineContentBatchBytes = 1 << 20;

    // The batch frames queued between the read and the pipe write: the read stalls until the pipe has taken one.
    const int InlineContentBatchesInFlight = 4;

    // Frames, in order: InlineContentBatch* then InlineContentCompleted once every request has been answered, or
    // Error at any point. A cancelled read (its pipe closed, or the session ended) writes nothing more. The read runs
    // on a thread-pool thread and queues each full batch for this channel's pipe writer, so a slow client holds back
    // only this read. The pipe publishes Processing when the read starts and for every answer, and every frame write
    // restarts the progress clock too, so a read that keeps answering keeps its channel alive even before a batch fills.
    // A read is not admitted to the parse-thread budget: it parses nothing and reads only the requested records.
    async Task RunInlineContentAsync(VolumeChannel channel, BrokerFrame request, CancellationToken cancellationToken)
    {
        try
        {
            channel.Pipe.Processing(InlineContentStep);
            using var read = new InlineContentRead(channel, cancellationToken);
            var pump = Task.Run(read.PumpBatchesAsync, CancellationToken.None);

            Exception? readFailure = null;
            try
            {
                await read.ReadAsync(_volumeAccess, request.ExpectedVolumeSerial, request.InlineContentRequests)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                readFailure = exception;
            }
            finally
            {
                read.CompleteBatches();
            }

            // A pipe failure the pump saw wins over the cancellation it caused in the read.
            await pump.ConfigureAwait(false);
            if (readFailure is not null)
            {
                ExceptionDispatchInfo.Throw(readFailure);
            }

            await channel.Pipe.WriteFrameAsync(
                writer => BrokerProtocol.WriteInlineContentCompleted(writer, BrokerFrame.InlineContentCompleted()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not ClientDisconnectedException)
        {
            await WriteChannelErrorAsync(channel, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    // One read's state: the answers the volume read produces on its thread-pool thread are gathered into batch frames
    // of about InlineContentBatchBytes payload bytes, each queued behind a credit for the pump that writes them in order
    // and returns the credit. A failed pipe write cancels the read, so it cannot wait on a credit that will not come.
    sealed class InlineContentRead : IDisposable
    {
        readonly VolumeChannel _channel;
        readonly CancellationToken _channelToken;
        readonly CancellationTokenSource _cancellation;
        readonly Channel<BrokerFrame> _batches = Channel.CreateUnbounded<BrokerFrame>(
            new UnboundedChannelOptions { SingleReader = true });
        readonly SemaphoreSlim _credits = new(InlineContentBatchesInFlight);
        readonly List<BrokerInlineContentAnswer> _answers = [];
        readonly ArrayBufferWriter<byte> _content = new();
        int _payloadBytes;

        public InlineContentRead(VolumeChannel channel, CancellationToken channelToken)
        {
            _channel = channel;
            _channelToken = channelToken;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(channelToken);
        }

        /// <summary>Cancelled when the channel is, and when the pump fails to write.</summary>
        public CancellationToken Token => _cancellation.Token;

        public Task ReadAsync(IVolumeAccess volumeAccess, ulong expectedVolumeSerial, ReadOnlyMemory<InlineContentRequest> requests) =>
            Task.Run(() =>
            {
                volumeAccess.ReadInlineContent(_channel.Volume, expectedVolumeSerial, requests, Add, Token);
                Flush();
            }, Token);

        public void CompleteBatches() => _batches.Writer.TryComplete();

        public async Task PumpBatchesAsync()
        {
            try
            {
                await foreach (var batch in _batches.Reader.ReadAllAsync(_channelToken).ConfigureAwait(false))
                {
                    await _channel.Pipe.WriteFrameAsync(
                        writer => BrokerProtocol.WriteInlineContentBatch(writer, batch), _channelToken)
                        .ConfigureAwait(false);
                    _credits.Release();
                }
            }
            catch
            {
                await _cancellation.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        public void Dispose()
        {
            _cancellation.Dispose();
            _credits.Dispose();
        }

        void Add(int position, InlineContentOutcome outcome, ReadOnlySpan<byte> content)
        {
            var answerBytes = BrokerProtocol.InlineContentAnswerBytes(content.Length);
            if (_answers.Count != 0 && _payloadBytes + answerBytes > InlineContentBatchBytes)
            {
                Flush();
            }

            _answers.Add(new BrokerInlineContentAnswer(position, outcome, _content.WrittenCount, content.Length));
            _content.Write(content);
            _payloadBytes += answerBytes;
            _channel.Pipe.Processing(InlineContentStep);
        }

        void Flush()
        {
            if (_answers.Count == 0)
            {
                return;
            }

            _credits.Wait(Token);
            _channel.Pipe.Processing(InlineContentStep);
            _batches.Writer.TryWrite(BrokerFrame.InlineContentBatch([.. _answers], _content.WrittenMemory.ToArray()));
            _answers.Clear();
            _content.ResetWrittenCount();
            _payloadBytes = 0;
        }
    }
}
