using System.Runtime.CompilerServices;
using MFTLib.Index;

namespace Benchmark;

/// <summary>
///     One delta growth of a block, composed from the copier's phases so each is timed on its own: the reference
///     count, the replacement's creation, the row copy, the name copy (verbatim, or compacting), the header carry, the
///     posting build and the flush. A verbatim copy keeps the entry capacity, so it copies the bucket region; the
///     bucket rebuild a growth that changes the entry capacity runs instead is timed afterwards on the same replacement.
/// </summary>
static class GrowthCopyPhases
{
    /// <summary>Copies <paramref name="source" /> into a new block at <paramref name="targetPath" />, which is deleted afterwards.</summary>
    /// <param name="source">A completed block no one writes while it is copied.</param>
    /// <param name="targetPath">Where the replacement is created, as a persistent file so the flush writes it out.</param>
    /// <param name="compacting">True stores only referenced names (<see cref="NameCompaction" />); false copies every name.</param>
    /// <param name="clock">The clock every phase is timed with.</param>
    internal static GrowthPhaseTimings Measure(BlockFile source, string targetPath, bool compacting, TimeProvider clock)
    {
        var lap = new Lap(clock);
        using var references = NameReferences.Count(source);
        var count = lap.Next();
        var plan = WatchGrowthPlan.Plan(WatchGrowthPlan.CapacitiesOf(source), source.RowCount, WriteRefusal.Delta, 0,
            references.Usage);
        try
        {
            lap.Next();
            using var target = CreateReplacement(source, targetPath, plan.Capacities);
            var create = lap.Next();
            using var sourceAccess = source.TakeAccess();
            using var targetAccess = target.TakeAccess();
            BlockCopier.EnsureFits(source, target);
            lap.Next();
            BlockCopier.CopyRows(source, target);
            var copyRows = lap.Next();
            if (compacting)
            {
                NameCompaction.CopyNames(source, target, references, source.RowCount);
            }
            else
            {
                BlockCopier.CopyNames(source, target);
            }

            var copyNames = lap.Next();
            BlockCopier.CarryHeader(source, target);
            var carryHeader = lap.Next();
            PostingBuilder.Build(target, null, CancellationToken.None);
            var postingBuild = lap.Next();
            target.Header.Flags |= BlockFlags.Complete;
            var bytesWritten = UsedBytes(target);
            lap.Next();
            target.FlushUnderHeldAccess(null);
            var flush = lap.Next();
            double? bucketRebuild = null;
            if (!compacting)
            {
                NamePool.RebuildBuckets(target);
                bucketRebuild = lap.Next();
            }

            return new GrowthPhaseTimings(count, create, copyRows, copyNames, carryHeader, postingBuild, flush,
                bucketRebuild, bytesWritten, references.Usage, plan.CompactsNames);
        }
        finally
        {
            File.Delete(targetPath);
        }
    }

    /// <summary>
    ///     The bytes a growth writes into the used part of its replacement: rows and sequence numbers, name entries
    ///     (reserved entry zero included), pool bytes, the bucket directory, and the posting base's offsets and rows.
    /// </summary>
    internal static long UsedBytes(BlockFile block)
    {
        long rows = block.RowCount;
        long names = block.NameEntryCount;
        return rows * (Unsafe.SizeOf<FileRow>() + sizeof(ushort)) + (names + 1) * Unsafe.SizeOf<NameEntry>() +
               block.NamePoolUsed + (long)block.BucketCount * sizeof(uint) + (names + 2) * sizeof(uint) +
               rows * sizeof(uint);
    }

    static BlockFile CreateReplacement(BlockFile source, string path, BlockCapacities capacities) =>
        BlockFile.Create(new BlockFileCreateOptions
        {
            Path = path,
            Root = GrowthSyntheticVolume.Volume,
            VolumeSerial = source.Header.VolumeSerial,
            ProducerKind = source.Header.ProducerKind,
            RootRow = source.Header.RootRow,
            SlotCapacity = capacities.SlotCapacity,
            NameEntryCapacity = capacities.NameEntryCapacity,
            NamePoolCapacity = capacities.NamePoolCapacity,
            DeltaCapacity = capacities.DeltaCapacity,
            CacheTag = source.Header.CacheTag,
            ScanShape = source.Header.ScanShape
        });

    /// <summary>Consecutive intervals on one clock: each call returns the time since the previous one, or since creation.</summary>
    sealed class Lap(TimeProvider clock)
    {
        long _mark = clock.GetTimestamp();

        internal double Next()
        {
            var now = clock.GetTimestamp();
            var elapsed = clock.GetElapsedTime(_mark, now).TotalMilliseconds;
            _mark = now;
            return elapsed;
        }
    }
}

/// <summary>The phases of one timed growth, in milliseconds, with what it wrote and what the default plan decided.</summary>
/// <param name="Count">The name reference count.</param>
/// <param name="Create">Creating and mapping the empty replacement.</param>
/// <param name="CopyRows">Rows and sequence numbers.</param>
/// <param name="CopyNames">Name entries and pool bytes, with the bucket copy or, compacting, the chain links and the row rewrite.</param>
/// <param name="CarryHeader">The header fields.</param>
/// <param name="PostingBuild">The fresh posting base.</param>
/// <param name="Flush">Writing the replacement's pages out, which a watch growth runs off the pause.</param>
/// <param name="BucketRebuild">Rebuilding the bucket chains, which a verbatim growth runs only when the entry capacity changes; null when compacting, which links its chains as it stores names.</param>
/// <param name="BytesWritten">The used bytes written into the replacement (<see cref="GrowthCopyPhases.UsedBytes" />).</param>
/// <param name="Names">The source's stored and referenced names.</param>
/// <param name="PlanCompacts">Whether the default plan compacts a delta growth of this source.</param>
sealed record GrowthPhaseTimings(double Count, double Create, double CopyRows, double CopyNames, double CarryHeader,
    double PostingBuild, double Flush, double? BucketRebuild, long BytesWritten, NameUsage Names, bool PlanCompacts)
{
    /// <summary>Everything the watch waits for: all phases but the flush and the bucket rebuild this growth does not run.</summary>
    internal double Pause => Count + Create + CopyRows + CopyNames + CarryHeader + PostingBuild;
}
