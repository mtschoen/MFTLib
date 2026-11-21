using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Reads inline content in this process through <see cref="IVolumeAccess" />, for a caller that can already open
///     the volume. The read runs on a thread-pool thread, so the handler never runs on the caller's stack.
/// </summary>
sealed class VolumeInlineContentReader(IVolumeAccess volumeAccess) : IInlineContentReader
{
    public Task ReadAsync(VolumeKey volume, ulong volumeSerial, ReadOnlyMemory<InlineContentRequest> requests,
        InlineContentSink sink, CancellationToken cancellationToken) =>
        Task.Run(() => volumeAccess.ReadInlineContent(volume, volumeSerial, requests, sink, cancellationToken),
            cancellationToken);
}
