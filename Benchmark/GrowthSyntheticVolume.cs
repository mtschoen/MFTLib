using System.Globalization;
using MFTLib;
using MFTLib.Index;

namespace Benchmark;

/// <summary>
///     The synthetic MFT volume the growth benchmark grows. Rows 6 onward are files under the root, row 5, and share
///     their names in pairs or more; the block has the volume plan of its row count, so its delta is one eighth of its
///     slots. Journal changes come in two shapes: the measured mix of a real volume's exhausted block, and a burst of
///     creates. Both reuse record numbers in the slot headroom, as temporary files do, so no change needs more slots.
/// </summary>
sealed class GrowthSyntheticVolume
{
    internal const ulong JournalIdentifier = 7;
    internal const long StartUsn = 4096;
    internal const int BatchSize = 4096;
    const uint RootRow = 5;
    const uint FirstFileRow = 6;

    /// <summary>Six reserved rows and four files: two names shared in pairs, the fewest a rename can alternate between.</summary>
    internal const uint MinimumRows = 10;

    /// <summary>New names one real volume's journal interned before its delta filled (MFTLib issue 953).</summary>
    const uint MeasuredNewNames = 77_452;

    /// <summary>Of <see cref="MeasuredNewNames" />, the names no row referenced when the delta filled.</summary>
    const uint MeasuredUnusedNames = 43_571;

    static readonly DateTime Moment = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    internal static readonly VolumeKey Volume = new('T');

    internal GrowthSyntheticVolume(uint rows)
    {
        if (rows < MinimumRows)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "The growth benchmark needs file rows to share names.");
        }

        Rows = rows;
        Capacities = BlockLayout.PlanCapacities(rows, default);
        DistinctNames = Math.Min((rows - FirstFileRow) / 2, Capacities.NameEntryCapacity / 2);
    }

    /// <summary>Rows written, the root's included.</summary>
    internal uint Rows { get; }

    /// <summary>The volume plan of <see cref="Rows" />.</summary>
    internal BlockCapacities Capacities { get; }

    /// <summary>Distinct file names: half the file rows, or half the planned name entries when that is fewer.</summary>
    internal uint DistinctNames { get; }

    /// <summary>The slots past the written rows, where creates reuse record numbers.</summary>
    internal uint HeadroomRows => Capacities.SlotCapacity - Rows;

    /// <summary>
    ///     Writes and completes the block at <paramref name="path" />, the canonical file a cache-only open adopts, with
    ///     the journal cursor the changes continue from.
    /// </summary>
    internal void Write(string path)
    {
        using var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = path,
            Root = Volume,
            VolumeSerial = 0,
            ProducerKind = ProducerKind.Mft,
            RootRow = RootRow,
            SlotCapacity = Capacities.SlotCapacity,
            NameEntryCapacity = Capacities.NameEntryCapacity,
            NamePoolCapacity = Capacities.NamePoolCapacity,
            DeltaCapacity = Capacities.DeltaCapacity,
            ScanShape = ScanShape.ForMft(includeFreed: false)
        });
        var writer = new BlockWriter(block);
        var directory = new RowColumns(RootRow, RowFlags.InUse | RowFlags.Directory,
            (uint)FileAttributes.Directory, 0, Moment.Ticks, 1);
        Require(writer.TryWriteRow(RootRow, ".", in directory));
        var names = Enumerable.Range(0, (int)DistinctNames).Select(index => BaseName((uint)index)).ToArray();
        for (var row = FirstFileRow; row < Rows; row++)
        {
            var file = new RowColumns(RootRow, RowFlags.InUse, (uint)FileAttributes.Archive, row, Moment.Ticks, 1);
            Require(writer.TryWriteRow(row, names[(row - FirstFileRow) % DistinctNames], in file));
        }

        writer.SetJournalCursor(JournalIdentifier, StartUsn);
        writer.Complete(Moment, null);
    }

    /// <summary>
    ///     The measured mix, one name change per delta entry, so the block's delta is exactly full afterwards: half the
    ///     changes create a file under a new name, the other half rename a file to an existing name. Creates cycle over
    ///     as many headroom records as keep the measured share of new names referenced, so the rest end unused.
    /// </summary>
    internal IEnumerable<UsnJournalEntry[]> MixBatches()
    {
        var changes = Capacities.DeltaCapacity;
        var creates = (changes + 1) / 2;
        var records = (uint)Math.Max(1, (ulong)creates * (MeasuredNewNames - MeasuredUnusedNames) / MeasuredNewNames);
        return Batch(changes, (change, usn) => change % 2 == 0
            ? Create(change / 2, records, NewName("temp", change / 2), usn)
            : RenameToExistingName(change / 2, usn));
    }

    /// <summary>
    ///     A burst of <paramref name="creates" /> creates over the whole slot headroom; every other create interns a new
    ///     name and the rest reuse an existing one.
    /// </summary>
    internal IEnumerable<UsnJournalEntry[]> BurstBatches(uint creates) =>
        Batch(creates, (create, usn) => Create(create, HeadroomRows,
            create % 2 == 0 ? NewName("burst", create) : BaseName(create % DistinctNames), usn));

    static string BaseName(uint index) => string.Create(CultureInfo.InvariantCulture, $"file-{index:D10}");

    static string NewName(string prefix, uint index) => string.Create(CultureInfo.InvariantCulture, $"{prefix}-{index:D10}");

    static void Require(WriteRefusal refusal)
    {
        if (refusal != WriteRefusal.None)
        {
            throw new InvalidOperationException($"The synthetic growth block refused a row: {refusal}.");
        }
    }

    static IEnumerable<UsnJournalEntry[]> Batch(uint count, Func<uint, long, UsnJournalEntry> entry)
    {
        for (uint start = 0; start < count; start += BatchSize)
        {
            var batch = new UsnJournalEntry[Math.Min(BatchSize, count - start)];
            for (var offset = 0u; offset < batch.Length; offset++)
            {
                batch[offset] = entry(start + offset, StartUsn + 1 + start + offset);
            }

            yield return batch;
        }
    }

    /// <summary>Create number <paramref name="create" /> on one of <paramref name="records" /> headroom records; each reuse is a new incarnation.</summary>
    UsnJournalEntry Create(uint create, uint records, string name, long usn) => Entry(Rows + create % records,
        (ushort)(create / records + 1), name, UsnReason.FileCreate | UsnReason.Close, usn);

    /// <summary>
    ///     Rename number <paramref name="rename" /> of a file that shares its name, so neither the name it leaves nor the
    ///     one it takes becomes unused: it alternates between its next neighbour's name and its own.
    /// </summary>
    UsnJournalEntry RenameToExistingName(uint rename, long usn)
    {
        var name = rename % DistinctNames;
        var pass = rename / DistinctNames;
        var target = pass % 2 == 0 ? (name + 1) % DistinctNames : name;
        return Entry(FirstFileRow + name, 1, BaseName(target), UsnReason.RenameNewName | UsnReason.Close, usn);
    }

    static UsnJournalEntry Entry(uint record, ushort sequence, string name, UsnReason reason, long usn) =>
        UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = record,
            ParentRecordNumber = RootRow,
            SequenceNumber = sequence,
            FileName = name,
            Reason = reason,
            Usn = usn,
            TimestampUtc = Moment,
            FileAttributes = FileAttributes.Archive
        });
}
