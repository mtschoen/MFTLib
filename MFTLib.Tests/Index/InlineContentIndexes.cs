using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.Index;

/// <summary>
///     A source whose reader is a script, so a test sees exactly which records <see cref="FileIndex.ReadInlineContentAsync" />
///     asks for and answers them as it likes. It reads nothing from any volume.
/// </summary>
sealed class ScriptedInlineContentReader : IInlineContentReader
{
    /// <summary>The requests of every call, in the order the reader received them.</summary>
    internal List<InlineContentRequest[]> Calls { get; } = [];

    internal List<VolumeKey> Volumes { get; } = [];

    /// <summary>Answers a request. The default returns the record number as eight bytes.</summary>
    internal Func<InlineContentRequest, (InlineContentOutcome Outcome, byte[] Content)> Answer { get; init; } =
        request => (InlineContentOutcome.Returned, BitConverter.GetBytes(request.RecordNumber));

    /// <summary>Runs before the first answer; a test that cancels or fails the read does so here.</summary>
    internal Func<CancellationToken, Task>? Before { get; init; }

    public async Task ReadAsync(VolumeKey volume, ulong volumeSerial, ReadOnlyMemory<InlineContentRequest> requests,
        InlineContentSink sink, CancellationToken cancellationToken)
    {
        Calls.Add(requests.ToArray());
        Volumes.Add(volume);
        if (Before is { } before)
        {
            await before(cancellationToken).ConfigureAwait(false);
        }

        foreach (var request in requests.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (outcome, content) = Answer(request);
            sink(request.Position, outcome, content);
        }
    }
}

/// <summary>
///     Opens indexes over a synthetic dump block with a scripted reader. Rows: 5 root, 6 <c>documents</c>
///     directory, 7 <c>Notes.txt</c> (sequence 3), 8 <c>leaf.txt</c> (sequence 4), 9 <c>empty.txt</c>, and 10
///     <c>gone.txt</c>, a deleted file.
/// </summary>
static class InlineContentIndexes
{
    internal const char DriveLetter = 'D';

    static readonly string DumpFilePath = Path.Combine(AppContext.BaseDirectory, "mftlib-inline-synthetic.mft");

    static readonly DateTime Moment = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);

    internal static MftIndexSource Source(IInlineContentReader? reader) =>
        new(Produce, dumpIdentity: new MftDumpSourceIdentity(DumpFilePath, DriveLetter), inlineContentReader: reader);

    internal static Task<FileIndex> OpenAsync(IInlineContentReader? reader) =>
        FileIndex.OpenAsync(new FileIndexOptions { MftSource = Source(reader) }, CancellationToken.None);

    /// <summary>
    ///     Opens the same rows as the uncached root of synthetic volume <see cref="DriveLetter" />, rooted at
    ///     <paramref name="volumeRootDirectory" />, whose block records <paramref name="volumeSerial" />.
    /// </summary>
    internal static Task<FileIndex> OpenVolumeAsync(IInlineContentReader? reader, ulong volumeSerial,
        string volumeRootDirectory) =>
        FileIndex.OpenAsync(SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = new VolumeKey(DriveLetter),
            CacheMode = CacheMode.Disabled,
            MftSource = new MftIndexSource(Produce, inlineContentReader: reader)
        }, volumeSerial, volume: new VolumeKey(DriveLetter), volumeRootDirectory: volumeRootDirectory),
            CancellationToken.None);

    /// <summary>The entry of the row with this record number, deleted ones included.</summary>
    internal static FileEntry Entry(FileIndex index, uint record) =>
        index.Search(new SearchQuery(null, IncludeDeleted: true)).Single(entry => entry.RecordKey.RecordNumber == record);

    static Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken _) =>
        Task.FromResult(new MftBlockProduceResult(Build(request), SkippedRecordCount: 0));

    static BlockFile Build(MftBlockProduceRequest request) =>
        SyntheticBlock.Build(request.BlockPath, request.Volume, request.VolumeSerial, request.DeleteOnClose,
            new SyntheticBlockOptions
            {
                JournalCursor = new SyntheticJournalCursor(7, 4096),
                ScanTimestamp = Moment,
                CacheTag = request.CacheTag
            }, Rows());

    static IEnumerable<SyntheticRow> Rows()
    {
        yield return new SyntheticRow(5, ".", 5) { IsDirectory = true, LastWriteTime = Moment, SequenceNumber = 5 };
        yield return new SyntheticRow(6, "documents", 5) { IsDirectory = true, LastWriteTime = Moment, SequenceNumber = 2 };
        yield return new SyntheticRow(7, "Notes.txt", 6) { Size = 11, LastWriteTime = Moment, SequenceNumber = 3 };
        yield return new SyntheticRow(8, "leaf.txt", 6) { Size = 22, LastWriteTime = Moment, SequenceNumber = 4 };
        yield return new SyntheticRow(9, "empty.txt", 6) { Size = 0, LastWriteTime = Moment, SequenceNumber = 1 };
        yield return new SyntheticRow(10, "gone.txt", 6)
        {
            Size = 5,
            LastWriteTime = Moment,
            SequenceNumber = 6,
            IsTombstone = true
        };
    }
}
