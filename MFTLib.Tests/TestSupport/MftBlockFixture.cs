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
        using var block = Build(new MftBlockProduceRequest
        {
            DriveLetter = 'C',
            VolumeSerial = volumeSerial,
            BlockPath = path,
            DeleteOnClose = deleteOnClose
        }, journalId, nextUsn, moment);
    }

    /// <summary>
    ///     Builds the block for <paramref name="request" /> and returns the open live handle, the way
    ///     a producer is expected to transfer ownership.
    /// </summary>
    public static BlockFile Build(MftBlockProduceRequest request, ulong journalId, long nextUsn, DateTime moment)
    {
        var createOptions = new BlockFileCreateOptions
        {
            Path = request.BlockPath,
            VolumeSerial = request.VolumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(8),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(256),
            DeleteOnClose = request.DeleteOnClose,
            CacheTag = request.CacheTag
        };

        var block = BlockFile.Create(createOptions);
        try
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "$MFT",
                new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 0, Size: 0,
                    ModifiedTicks: moment.Ticks, SequenceNumber: 0));
            writer.TryWriteRow(5, ".",
                new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0, Size: 0,
                    ModifiedTicks: moment.Ticks, SequenceNumber: 0));
            writer.SetJournalCursor(journalId, nextUsn);
            writer.Complete(moment, null);
            return block;
        }
        catch
        {
            block.Dispose();
            throw;
        }
    }

    public static BlockFile Build(MftBlockProduceRequest request, uint rowCount, Func<uint, string> fileNameFactory,
        DateTime moment)
    {
        var createOptions = new BlockFileCreateOptions
        {
            Path = request.BlockPath,
            VolumeSerial = request.VolumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(rowCount + 8),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity((rowCount + 8) * 32),
            DeleteOnClose = request.DeleteOnClose,
            CacheTag = request.CacheTag
        };

        var block = BlockFile.Create(createOptions);
        try
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "$MFT",
                new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 0, Size: 0,
                    ModifiedTicks: moment.Ticks, SequenceNumber: 0));
            writer.TryWriteRow(5, ".",
                new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0,
                    Size: 0, ModifiedTicks: moment.Ticks, SequenceNumber: 0));

            for (var rowIndex = 6u; rowIndex < rowCount + 6; rowIndex++)
            {
                writer.TryWriteRow(rowIndex, fileNameFactory(rowIndex),
                    new RowColumns(ParentRow: 5, Flags: RowFlags.InUse, Attributes: 0, Size: rowIndex,
                        ModifiedTicks: moment.Ticks, SequenceNumber: 0));
            }

            writer.SetJournalCursor(7, 4096);
            writer.Complete(moment, null);
            return block;
        }
        catch
        {
            block.Dispose();
            throw;
        }
    }

    /// <summary>An MFT producer result over <see cref="Build(MftBlockProduceRequest, ulong, long, DateTime)" />, reporting the same cursor it stamped.</summary>
    public static Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, ulong journalId, long nextUsn,
        DateTime moment)
    {
        return Task.FromResult(new MftBlockProduceResult(
            Build(request, journalId, nextUsn, moment),
            journalId, nextUsn, SkippedRecordCount: 0));
    }
}
