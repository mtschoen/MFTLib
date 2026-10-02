using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A small valid MFT-kind block built with the production <see cref="BlockWriter" />: row 0
///     (<c>$MFT</c>) and root row 5 (<c>.</c>), matching <see cref="MFTLib.Tests.Index.SyntheticBlockBuilder.MftShaped" />'s
///     root convention, with the journal cursor stamped through <see cref="BlockWriter.SetJournalCursor" />
///     before <see cref="BlockWriter.Complete" />, the same flush-safe order a real MFT producer follows.
///     Every caller supplies its own cursor and timestamp; nothing here is shared mutable state.
/// </summary>
internal static class MftBlockFixture
{
    /// <summary>The completion and row timestamp of blocks seeded straight onto disk by rescan and cache tests.</summary>
    public static readonly DateTime SeededMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Writes the block at <paramref name="path" /> and closes it, leaving only the file on disk.</summary>
    public static void Write(string path, uint volumeSerial, ulong journalId, long nextUsn, DateTime moment,
        bool deleteOnClose = false)
    {
        var createOptions = new BlockFileCreateOptions
        {
            Path = path,
            VolumeSerial = volumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(8),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(256),
            DeleteOnClose = deleteOnClose
        };

        using var block = BlockFile.Create(createOptions);
        var writer = new BlockWriter(block);
        writer.TryWriteRow(0, "$MFT",
            new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 0, Size: 0,
                ModifiedTicks: moment.Ticks, SequenceNumber: 0));
        writer.TryWriteRow(5, ".",
            new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0, Size: 0,
                ModifiedTicks: moment.Ticks, SequenceNumber: 0));
        writer.SetJournalCursor(journalId, nextUsn);
        writer.Complete(moment, null);
    }

    /// <summary>
    ///     Writes the block at <paramref name="request" />'s path, honoring its delete-on-close flag,
    ///     then reopens it as a fresh handle, the way a real producer's caller adopts the block it wrote.
    /// </summary>
    public static BlockFile WriteAndOpen(MftBlockProduceRequest request, ulong journalId, long nextUsn,
        DateTime moment)
    {
        Write(request.BlockPath, request.VolumeSerial, journalId, nextUsn, moment, request.DeleteOnClose);
        return BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!;
    }

    /// <summary>An MFT producer result over <see cref="WriteAndOpen" />, reporting the same cursor it stamped.</summary>
    public static Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, ulong journalId, long nextUsn,
        DateTime moment)
    {
        return Task.FromResult(new MftBlockProduceResult(
            WriteAndOpen(request, journalId, nextUsn, moment),
            journalId, nextUsn, SkippedRecordCount: 0));
    }
}
