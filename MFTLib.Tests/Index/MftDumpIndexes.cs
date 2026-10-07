using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Opens indexes over a synthetic MFT dump: a source built through the internal constructor
///     with a producer that writes a small block, the way the dump producer of a later slice will.
///     Rows: 5 is the root, 6 <c>documents</c>, 7 <c>Notes.txt</c> and 8 <c>Deep</c> under 6, and 9
///     <c>leaf.txt</c> under 8.
/// </summary>
internal static class MftDumpIndexes
{
    internal const char DriveLetter = 'D';

    internal static readonly string Root = MftDumpPaths.CanonicalRoot(DriveLetter);

    internal static readonly string DumpFilePath = Path.Combine(Path.GetTempPath(), "mftlib-synthetic.mft");

    static readonly DateTime Moment = new(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

    internal static MftIndexSource Source(MftBlockProducer? producer = null) =>
        new(producer ?? Produce, dumpIdentity: new MftDumpSourceIdentity(DumpFilePath, DriveLetter));

    internal static FileIndexOptions Options(MftIndexSource? source = null) => new()
    {
        Drives = [new IndexedDrive(DriveLetter, Root, 0)],
        NoCache = true,
        MftSource = source ?? Source()
    };

    internal static Task<FileIndex> OpenAsync(MftIndexSource? source = null) =>
        FileIndex.OpenAsync(Options(source), CancellationToken.None);

    internal static Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken _) =>
        Task.FromResult(new MftBlockProduceResult(Build(request), 7, 4096, SkippedRecordCount: 0));

    /// <summary>Runs <paramref name="action" /> on the index and returns the exception it throws.</summary>
    internal static TException Throws<TException>(FileIndex index, Action<FileIndex> action)
        where TException : Exception =>
        Assert.ThrowsException<TException>(() => action(index));

    /// <summary>Awaits <paramref name="action" /> on the index and returns the exception it throws.</summary>
    internal static Task<TException> ThrowsAsync<TException>(FileIndex index, Func<FileIndex, Task> action)
        where TException : Exception =>
        Assert.ThrowsExceptionAsync<TException>(() => action(index));

    static BlockFile Build(MftBlockProduceRequest request) =>
        SyntheticBlock.Build(request.BlockPath, request.VolumeSerial, request.DeleteOnClose,
            new SyntheticBlockOptions
            {
                JournalCursor = new SyntheticJournalCursor(7, 4096),
                CompletedUtc = Moment,
                CacheTag = request.CacheTag
            }, Rows());

    internal static Task<MftBlockProduceResult> Fail(MftBlockProduceRequest request, CancellationToken _) =>
        Task.FromException<MftBlockProduceResult>(new IOException("The dump file cannot be opened."));

    static IEnumerable<SyntheticRow> Rows()
    {
        yield return new SyntheticRow(0, "$MFT", 0) { ModifiedUtc = Moment };
        yield return new SyntheticRow(5, ".", 5) { IsDirectory = true, ModifiedUtc = Moment };
        yield return new SyntheticRow(6, "documents", 5) { IsDirectory = true, ModifiedUtc = Moment };
        yield return new SyntheticRow(7, "Notes.txt", 6) { Size = 11, ModifiedUtc = Moment };
        yield return new SyntheticRow(8, "Deep", 6) { IsDirectory = true, ModifiedUtc = Moment };
        yield return new SyntheticRow(9, "leaf.txt", 8) { Size = 22, ModifiedUtc = Moment };
    }
}
