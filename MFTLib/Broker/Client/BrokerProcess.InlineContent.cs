using MFTLib.Index;

namespace MFTLib;

/// <summary>Reads the resident content of specific records of one volume through its own channels.</summary>
sealed partial class BrokerProcess
{
    /// <summary>
    ///     Reads the requested records on volume channels, one channel per chunk of at most
    ///     <see cref="BrokerProtocol.MaximumInlineContentRequestsPerFrame" /> requests, so each request frame stays
    ///     under the frame limit. Chunks run one after another in request order, so the sink is called serially.
    ///     On success every request is answered exactly once. Each chunk's channel opens and checks the volume before that
    ///     chunk's first answer, so a failure of a later chunk ends the read after the earlier chunks' answers.
    /// </summary>
    /// <param name="volume">The volume whose MFT holds the records.</param>
    /// <param name="expectedVolumeSerial">The serial of the volume the index block represents.</param>
    /// <param name="requests">The requests, ascending by record number, with distinct positions.</param>
    /// <param name="sink">Receives each answer; its content is valid only during the call. An exception it throws stops the read.</param>
    /// <param name="cancellationToken">Closes the channel of the chunk in flight.</param>
    /// <exception cref="InvalidOperationException">The broker reported the read failed.</exception>
    /// <exception cref="BrokerChannelLostException">A channel or the process was lost, a channel answered inconsistently, or the host reported a stall.</exception>
    /// <exception cref="TimeoutException">The broker did not answer the channel open within the reply timeout.</exception>
    internal async Task ReadInlineContentAsync(VolumeKey volume, ulong expectedVolumeSerial,
        ReadOnlyMemory<InlineContentRequest> requests, InlineContentSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        volume = volume.Validated();
        for (var start = 0; start < requests.Length; start += BrokerProtocol.MaximumInlineContentRequestsPerFrame)
        {
            var chunk = requests.Slice(start,
                Math.Min(BrokerProtocol.MaximumInlineContentRequestsPerFrame, requests.Length - start));
            await ReadInlineContentChunkAsync(volume, expectedVolumeSerial, chunk, sink, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    async Task ReadInlineContentChunkAsync(VolumeKey volume, ulong expectedVolumeSerial,
        ReadOnlyMemory<InlineContentRequest> chunk, InlineContentSink sink, CancellationToken cancellationToken)
    {
        var channel = await OpenChannelAsync(volume,
            writer => BrokerProtocol.WriteReadInlineContent(writer,
                BrokerFrame.ReadInlineContent(chunk, expectedVolumeSerial)),
            cancellationToken).ConfigureAwait(false);
        await using var ownedChannel = channel.ConfigureAwait(false);
        var frames = new BrokerChannelFrames(channel, "inline content");
        var outstanding = new OutstandingPositions(chunk.Span);

        while (true)
        {
            var frame = await frames.ReadAsync("InlineContentBatch or InlineContentCompleted", cancellationToken)
                .ConfigureAwait(false);
            switch (frame.Kind)
            {
                case BrokerFrameKind.InlineContentBatch:
                    DeliverInlineContentBatch(channel, frame, outstanding, sink, cancellationToken);
                    break;

                case BrokerFrameKind.InlineContentCompleted:
                    if (outstanding.Remaining != 0)
                    {
                        throw new BrokerChannelLostException(volume,
                            $"Volume {volume} inline content channel completed with {outstanding.Remaining} positions unanswered.");
                    }

                    await frames.ReadClosureAsync(frame, cancellationToken).ConfigureAwait(false);
                    return;

                default:
                    throw frames.OutOfOrder(frame, "InlineContentBatch or InlineContentCompleted");
            }
        }
    }

    // Hands one batch's answers to the sink, serially. A position the chunk did not ask for, or asked for fewer times,
    // is a channel that answered inconsistently; the requests carry distinct positions.
    static void DeliverInlineContentBatch(BrokerVolumeChannel channel, BrokerFrame frame,
        OutstandingPositions outstanding, InlineContentSink sink, CancellationToken cancellationToken)
    {
        var content = frame.InlineContent.Span;
        var delivered = 0;
        foreach (var answer in frame.InlineContentAnswers)
        {
            if (delivered++ % 4096 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!outstanding.TryAnswer(answer.Position))
            {
                throw new BrokerChannelLostException(channel.Volume,
                    $"Volume {channel.Volume} inline content channel answered position {answer.Position} that it was not asked for, or twice.");
            }

            sink(answer.Position, answer.Outcome, content.Slice(answer.Offset, answer.Length));
        }
    }
}
