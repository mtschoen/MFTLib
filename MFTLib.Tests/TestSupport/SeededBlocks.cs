using MFTLib.Index;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A small valid MFT-kind block: row 0 (<c>$MFT</c>) and root row 5 (<c>.</c>). A thin
///     forwarder to <see cref="SyntheticBlock" />, which owns the only block-writing code, with the
///     cursor, timestamp and cache tag a rescan or cache test seeds. Every caller supplies its own
///     cursor and timestamp; nothing here is shared mutable state.
/// </summary>
internal static class SeededBlocks
{
    /// <summary>The completion and row timestamp of blocks seeded straight onto disk by rescan and cache tests.</summary>
    public static readonly DateTime SeededMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Writes the block at <paramref name="path" /> and closes it, leaving only the file on disk.</summary>
    public static void Write(string path, uint volumeSerial, ulong journalIdentifier, long nextUsn, DateTime moment,
        bool deleteOnClose = false)
    {
        using var block = Create(path, volumeSerial, deleteOnClose, default, journalIdentifier, nextUsn, moment,
            RootRows(moment));
    }

    /// <summary>
    ///     Builds the block for <paramref name="request" /> and returns the open live handle, the way
    ///     a producer is expected to transfer ownership.
    /// </summary>
    public static BlockFile Build(MftBlockProduceRequest request, ulong journalIdentifier, long nextUsn, DateTime moment)
    {
        return Create(request.BlockPath, request.VolumeSerial, request.DeleteOnClose, request.CacheTag,
            journalIdentifier, nextUsn, moment, RootRows(moment));
    }

    /// <summary>Builds a block with <paramref name="rowCount" /> files named by <paramref name="fileNameFactory" /> under the root.</summary>
    public static BlockFile Build(MftBlockProduceRequest request, uint rowCount, Func<uint, string> fileNameFactory,
        DateTime moment)
    {
        var rows = RootRows(moment).Concat(Enumerable.Range(6, (int)rowCount).Select(index =>
            new SyntheticRow((uint)index, fileNameFactory((uint)index), 5)
            {
                Size = index,
                LastWriteTime = moment
            }));
        return Create(request.BlockPath, request.VolumeSerial, request.DeleteOnClose, request.CacheTag, 7, 4096,
            moment, rows);
    }

    /// <summary>An MFT producer result over <see cref="Build(MftBlockProduceRequest, ulong, long, DateTime)" />, reporting the same cursor it stamped.</summary>
    public static Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, ulong journalIdentifier, long nextUsn,
        DateTime moment)
    {
        return Task.FromResult(new MftBlockProduceResult(
            Build(request, journalIdentifier, nextUsn, moment),
            journalIdentifier, nextUsn, SkippedRecordCount: 0));
    }

    static BlockFile Create(string path, uint volumeSerial, bool deleteOnClose, CacheTag cacheTag,
        ulong journalIdentifier, long nextUsn, DateTime moment, IEnumerable<SyntheticRow> rows)
    {
        return SyntheticBlock.Build(path, volumeSerial, deleteOnClose, new SyntheticBlockOptions
        {
            JournalCursor = new SyntheticJournalCursor(journalIdentifier, nextUsn),
            ScanTimestamp = moment,
            CacheTag = cacheTag
        }, rows);
    }

    static IEnumerable<SyntheticRow> RootRows(DateTime moment)
    {
        yield return new SyntheticRow(0, "$MFT", 0) { LastWriteTime = moment };
        yield return new SyntheticRow(5, ".", 5) { IsDirectory = true, LastWriteTime = moment };
    }
}
