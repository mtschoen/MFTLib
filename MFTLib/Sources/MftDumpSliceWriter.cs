using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.NativeInterop;

namespace MFTLib;

/// <summary>
///     Step-two consumer: writes each streamed slice straight from its packed <c>MftCompactEntry</c> rows into the
///     block, with no <see cref="MftRecord" />, no per-record string and no batch list. It keeps the semantics of
///     <see cref="MftDumpRecordValidation" /> and <see cref="MftBlockRowWriter" /> for a dump scan: a record not in
///     use is dropped, a repeated base record number or a parent outside the block fails the import, a record or
///     parent number past 32 bits and an empty name are skipped and counted, and the root row is checked last.
/// </summary>
static class MftDumpSliceWriter
{
    const string NoRootMessage = "The dump has no valid allocated root record.";
    const string DuplicateRecordMessage = "The dump contains duplicate base record numbers.";
    const string RecordOutsideIndexMessage = "The dump contains required record numbers outside the index range.";
    const string StringOutsidePoolMessage = "Native MFT string offset is outside its pool";
    const uint RootRow = 5;
    const ushort InUseFlag = 1;
    const ushort DirectoryFlag = 2;
    const ushort SizeUnknownFlag = 0x8000;

    // DateTime.MaxValue as a FILETIME, past which MftRecord.ModifiedUtc reads MinValue.
    static readonly long MaximumFileTime = DateTime.MaxValue.ToFileTimeUtc();
    static readonly long FileTimeEpochTicks = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    sealed class State(uint slotCapacity)
    {
        internal readonly uint SlotCapacity = slotCapacity;
        internal readonly ulong[] SeenRows = new ulong[((ulong)slotCapacity + 63) / 64];
        internal HashSet<ulong>? SeenPastBlock;
        internal long RecordsWritten;
        internal long SkippedRecordCount;
    }

    /// <summary>
    ///     Starts the parse, writes every slice as it arrives, then applies the extension data sizes and checks the
    ///     root row. Stops early, without those last steps, when the name regions need a larger block.
    /// </summary>
    internal static BlockWriteResult Write(BlockWriter writer, MftDumpSliceStream stream,
        IProgress<BlockWriteProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var block = writer.Block;
        var state = new State(block.SlotCapacity);
        var stoppedForGrowth = false;
        stream.Start();
        try
        {
            foreach (var slice in stream.Slices())
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var access = block.TakeAccess();
                    stoppedForGrowth = WriteSlice(writer, access, slice, state);
                }
                finally
                {
                    slice.Return();
                }

                if (stoppedForGrowth)
                {
                    break;
                }

                progress?.Report(new BlockWriteProgress(state.RecordsWritten, block.NamePoolUsed, null, null));
            }

            if (!stoppedForGrowth)
            {
                stream.Finish();
                MftDumpSliceRecords.ApplyExtensionDataSizes(writer, stream.ExtensionDataSizes);
                ThrowUnlessRootRowIsValid(block);
            }
        }
        finally
        {
            stream.Stop();
        }

        return new BlockWriteResult(writer.RowCount, block.NamePoolUsed, state.SkippedRecordCount);
    }

    // True when a failed write left the name regions needing a larger block, so the scan stops.
    static unsafe bool WriteSlice(BlockWriter writer, BlockAccessScope access, MftDumpSlice slice, State state)
    {
        var poolUnits = (ulong)slice.StringUnits;
        fixed (byte* table = slice.Entries)
        fixed (char* pool = slice.Strings)
        {
            var row = table;
            for (var index = 0; index < slice.EntryCount; index++, row += MFTLibNative.NativeCompactEntrySize)
            {
                var recordNumber = Unsafe.ReadUnaligned<ulong>(row);
                var parentRecordNumber = Unsafe.ReadUnaligned<ulong>(row + 8);
                var stringOffset = Unsafe.ReadUnaligned<ulong>(row + 16);
                var stringLength = Unsafe.ReadUnaligned<ushort>(row + 30);
                if (stringOffset > poolUnits || stringLength > poolUnits - stringOffset)
                {
                    throw new InvalidDataException(StringOutsidePoolMessage);
                }

                var flags = Unsafe.ReadUnaligned<ushort>(row + 28);
                if ((flags & InUseFlag) == 0)
                {
                    continue;
                }

                if (!MarkSeen(state, recordNumber))
                {
                    throw new InvalidDataException(DuplicateRecordMessage);
                }

                if (parentRecordNumber >= state.SlotCapacity)
                {
                    throw new InvalidDataException(RecordOutsideIndexMessage);
                }

                if (TryWriteEntry(writer, access, row, recordNumber, parentRecordNumber, flags,
                        stringLength == 0
                            ? recordNumber == RootRow ? "." : ReadOnlySpan<char>.Empty
                            : new ReadOnlySpan<char>(pool + stringOffset, stringLength)))
                {
                    state.RecordsWritten++;
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

    static unsafe bool TryWriteEntry(BlockWriter writer, BlockAccessScope access, byte* row, ulong recordNumber,
        ulong parentRecordNumber, ushort flags, ReadOnlySpan<char> name)
    {
        if (recordNumber > uint.MaxValue || parentRecordNumber > uint.MaxValue || name.Length == 0)
        {
            return false;
        }

        var isDirectory = (flags & DirectoryFlag) != 0;
        var rowFlags = RowFlags.InUse;
        if (isDirectory)
        {
            rowFlags |= RowFlags.Directory;
        }

        if ((flags & SizeUnknownFlag) != 0)
        {
            rowFlags |= RowFlags.SizeUnknown;
        }

        var columns = new RowColumns((uint)parentRecordNumber, rowFlags, Unsafe.ReadUnaligned<uint>(row + 24),
            isDirectory ? 0 : Unsafe.ReadUnaligned<long>(row + 32), ModifiedTicks(Unsafe.ReadUnaligned<long>(row + 40)),
            Unsafe.ReadUnaligned<ushort>(row + 48));
        return writer.TryWriteRowUnderAccess(access, (uint)recordNumber, name, in columns);
    }

    // MftRecord.ModifiedUtc.Ticks: zero, negative and past-DateTime.MaxValue FILETIMEs read as DateTime.MinValue.
    static long ModifiedTicks(long fileTime) =>
        fileTime <= 0 || fileTime > MaximumFileTime ? DateTime.MinValue.Ticks : fileTime + FileTimeEpochTicks;

    static bool MarkSeen(State state, ulong recordNumber)
    {
        if (recordNumber >= state.SlotCapacity)
        {
            return (state.SeenPastBlock ??= []).Add(recordNumber);
        }

        ref var word = ref state.SeenRows[recordNumber / 64];
        var bit = 1UL << (int)(recordNumber % 64);
        if ((word & bit) != 0)
        {
            return false;
        }

        word |= bit;
        return true;
    }

    static void ThrowUnlessRootRowIsValid(BlockFile block)
    {
        using var access = block.TakeAccess();
        var root = block.Rows[(int)RootRow];
        if (!root.IsInUse || root.IsDeleted || !root.IsDirectory || root.ParentRow != RootRow)
        {
            throw new InvalidDataException(NoRootMessage);
        }
    }
}
