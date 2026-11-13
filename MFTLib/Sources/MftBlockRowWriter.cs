using MFTLib.Index;
using MFTLib.NativeInterop;

namespace MFTLib;

/// <summary>Which checks a scan's rows pass beyond the row writer's own skip rules.</summary>
enum MftRowValidation
{
    /// <summary>A live volume's rows, written as the volume reports them.</summary>
    None,

    /// <summary>An untrusted dump's rows, checked by <see cref="MftDumpRecordValidation" />.</summary>
    Dump
}

/// <summary>
///     Writes a scan's slices straight from their packed entries into sparse block rows, while the source is still
///     parsing later chunks, without resolving paths or materializing records. A record not in use is dropped unless
///     the scan includes freed records, which <see cref="FreedRowTrust" /> then judges once every row is written; a
///     record or parent number past 32 bits and an empty name are skipped and counted.
/// </summary>
static class MftBlockRowWriter
{
    /// <summary>The processing step each written slice publishes, so a host sees progress while rows are written.</summary>
    internal const string WriteStep = "block write";

    const uint RootRecord = 5;
    const string StringOutsidePoolMessage = "Native MFT string offset is outside its pool";

    // DateTime.MaxValue as a FILETIME, past which MftRecord.ModifiedUtc reads MinValue.
    static readonly long MaximumFileTime = DateTime.MaxValue.ToFileTimeUtc();
    static readonly long FileTimeEpochTicks = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    sealed class WriteState(BlockFile block, MftScanOptions scanOptions, MftRowValidation validation)
    {
        internal readonly FreedRowTrust? FreedRows = scanOptions.IncludeFreed ? new FreedRowTrust(block) : null;

        internal readonly MftDumpRecordValidation? DumpValidation =
            validation == MftRowValidation.Dump ? new MftDumpRecordValidation(block) : null;

        internal long SkippedRecordCount;
    }

    /// <summary>
    ///     Writes every slice as it arrives, checking each row as the source's <see cref="IMftSliceSource.Validation" />
    ///     asks, then applies the extension data sizes, judges freed rows and, for a dump, checks the root row; the caller stamps and completes the block. A write that leaves the name regions needing
    ///     a larger block stops the scan without those last steps, since the producer retries it into a larger block.
    /// </summary>
    /// <returns>The row count, name pool usage, and records skipped or left out by the source.</returns>
    /// <exception cref="InvalidDataException">A dump check failed, or a packed entry's name lies outside its pool.</exception>
    internal static BlockWriteResult Write(BlockWriter writer, IMftSliceSource source, MftScanOptions scanOptions,
        ISourceOperationReporter? operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        var block = writer.Block;
        var state = new WriteState(block, scanOptions, source.Validation);
        foreach (var slice in source.Slices())
        {
            bool stoppedForGrowth;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                operation?.Processing(WriteStep);
                using var access = block.TakeAccess();
                stoppedForGrowth = WriteSlice(writer, access, slice, state);
            }
            finally
            {
                slice.Return();
            }

            if (stoppedForGrowth)
            {
                return new BlockWriteResult(writer.RowCount, block.NamePoolUsed, state.SkippedRecordCount);
            }
        }

        var completion = source.Finish();
        ApplyExtensionDataSizes(writer, completion.ExtensionDataSizes);
        // A freed record's parent can arrive after it, so trust is decided once every row is written.
        state.FreedRows?.DetachUntrusted(writer, cancellationToken);
        state.DumpValidation?.ThrowUnlessRootRowIsValid();
        return new BlockWriteResult(writer.RowCount, block.NamePoolUsed,
            state.SkippedRecordCount + completion.RecordsOmitted);
    }

    // The sizes extension records carry, applied to the rows they name once every row is written.
    static void ApplyExtensionDataSizes(BlockWriter writer, MftExtensionDataSize[] sizes)
    {
        foreach (var extension in sizes)
        {
            if (extension.BaseRecordNumber <= uint.MaxValue)
            {
                writer.TryApplyScannedDataSize((uint)extension.BaseRecordNumber, extension.BaseSequenceNumber,
                    extension.Size);
            }
        }
    }

    // True when a failed write left the name regions needing a larger block, so the scan stops.
    static unsafe bool WriteSlice(BlockWriter writer, BlockAccessScope access, MftParsedSlice slice, WriteState state)
    {
        var poolUnits = (ulong)slice.StringUnits;
        fixed (byte* table = slice.Entries)
        fixed (char* pool = slice.Strings)
        {
            for (var index = 0; index < slice.EntryCount; index++)
            {
                var entry = new MftPackedEntry(table + (index * (int)MFTLibNative.NativeCompactEntrySize));
                var stringOffset = entry.StringOffset;
                var stringLength = entry.StringLength;
                if (stringOffset > poolUnits || stringLength > poolUnits - stringOffset)
                {
                    throw new InvalidDataException(StringOutsidePoolMessage);
                }

                if (!entry.InUse && state.FreedRows is null)
                {
                    continue;
                }

                state.DumpValidation?.Check(entry.RecordNumber, entry.ParentRecordNumber);
                state.FreedRows?.Observe(entry);
                var name = stringLength > 0
                    ? new ReadOnlySpan<char>(pool + stringOffset, stringLength)
                    : entry.RecordNumber == RootRecord ? "." : ReadOnlySpan<char>.Empty;
                if (TryWriteEntry(writer, access, entry, name))
                {
                    state.FreedRows?.Wrote(entry);
                }
                else
                {
                    state.SkippedRecordCount++;
                    if (writer.NameRegionsNeedGrowth)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // Writes one entry under its name; false when it is skipped and counted.
    static bool TryWriteEntry(BlockWriter writer, BlockAccessScope access, MftPackedEntry entry,
        ReadOnlySpan<char> name)
    {
        // Both identifiers are 48-bit on disk. A truncated parent would point the row at an
        // unrelated record, so an out-of-range record number and an out-of-range parent are
        // both skipped and counted rather than written.
        if (entry.RecordNumber > uint.MaxValue || entry.ParentRecordNumber > uint.MaxValue || name.Length == 0)
        {
            return false;
        }

        // A freed record has the flags a journal delete leaves, so a deleted row has one shape in a block.
        var flags = entry.InUse ? RowFlags.InUse : RowFlags.InUse | RowFlags.Tombstone;
        var isDirectory = entry.IsDirectory;
        if (isDirectory)
        {
            flags |= RowFlags.Directory;
        }

        if (!entry.SizeKnown)
        {
            flags |= RowFlags.SizeUnknown;
        }

        var columns = new RowColumns((uint)entry.ParentRecordNumber, flags, entry.FileAttributes,
            isDirectory ? 0 : entry.Size, ModifiedTicks(entry.ModifiedFileTime), entry.SequenceNumber);
        return writer.TryWriteRowUnderAccess(access, (uint)entry.RecordNumber, name, in columns);
    }

    // MftRecord.ModifiedUtc.Ticks: zero, negative and past-DateTime.MaxValue FILETIMEs read as DateTime.MinValue.
    static long ModifiedTicks(long fileTime) =>
        fileTime <= 0 || fileTime > MaximumFileTime ? DateTime.MinValue.Ticks : fileTime + FileTimeEpochTicks;
}
