namespace MFTLib.Index;

/// <summary>One record to read: where its answer goes, which record, and which incarnation the caller holds.</summary>
/// <param name="Position">The caller's position for the answer; handed back unchanged to the sink.</param>
/// <param name="RecordNumber">The MFT record number of the file's base record.</param>
/// <param name="SequenceNumber">The sequence number of the file incarnation the caller expects.</param>
readonly record struct InlineContentRequest(int Position, long RecordNumber, ushort SequenceNumber);

/// <summary>
///     Receives the answer for one request. <paramref name="content" /> is valid only during the call and is
///     empty unless <paramref name="outcome" /> is <see cref="InlineContentOutcome.Returned" />.
/// </summary>
delegate void InlineContentSink(int position, InlineContentOutcome outcome, ReadOnlySpan<byte> content);

/// <summary>
///     Reads the resident <c>$DATA</c> of specific MFT records of one volume. A local volume source, a dump block and
///     a broker source (through its broker's volume channels) implement this;
///     <see cref="FileIndex.ReadInlineContentAsync" /> is its only caller.
/// </summary>
interface IInlineContentReader
{
    /// <summary>
    ///     Answers every request exactly once through <paramref name="sink" />, serially and in unspecified order,
    ///     including requests past the end of the MFT. The completed task means every request was answered.
    /// </summary>
    /// <param name="volume">The volume whose MFT holds the records.</param>
    /// <param name="volumeSerial">The full serial the scanned block records; zero for a dump.</param>
    /// <param name="requests">The requests, ascending by <see cref="InlineContentRequest.RecordNumber" />.</param>
    /// <param name="sink">Receives each answer. An exception it throws stops the read and faults the task with it.</param>
    /// <param name="cancellationToken">Stops the read; the task then faults with <see cref="OperationCanceledException" />.</param>
    /// <exception cref="IOException">The volume or dump could not be opened or read.</exception>
    Task ReadAsync(VolumeKey volume, ulong volumeSerial, ReadOnlyMemory<InlineContentRequest> requests,
        InlineContentSink sink, CancellationToken cancellationToken);
}
