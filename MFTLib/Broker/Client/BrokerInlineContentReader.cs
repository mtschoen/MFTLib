using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Reads inline content through a broker, for a caller that cannot open the volume itself. Each read runs on the
///     process the connect function yields, on volume channels of their own; the caller keeps ownership of that process.
/// </summary>
/// <param name="connectAsync">Yields the process each read runs on.</param>
sealed class BrokerInlineContentReader(Func<CancellationToken, Task<BrokerProcess>> connectAsync) : IInlineContentReader
{
    public async Task ReadAsync(VolumeKey volume, ulong expectedVolumeSerial, ReadOnlyMemory<InlineContentRequest> requests,
        InlineContentSink sink, CancellationToken cancellationToken)
    {
        var process = await connectAsync(cancellationToken).ConfigureAwait(false);
        await process.ReadInlineContentAsync(volume, expectedVolumeSerial, requests, sink, cancellationToken)
            .ConfigureAwait(false);
    }
}
