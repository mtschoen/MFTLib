using System.Collections.Concurrent;
using MFTLib;
using MFTLib.Index;

namespace Benchmark;

/// <summary>
///     The synthetic block opened cache-only from a copy of its canonical file, with journal changes applied through
///     the real apply path (<see cref="FileIndex.ApplyJournalEntriesAsync(VolumeKey, IReadOnlyList{UsnJournalEntry}, ulong, long)" />,
///     the core a watch's pump runs). Every growth is observed at its publication; its pause is the time its batch took
///     beyond the median batch that did not grow.
/// </summary>
sealed class GrowthSession : IAsyncDisposable
{
    readonly FileIndex _index;
    readonly ConcurrentQueue<string> _diagnostics;
    readonly TimeProvider _clock;
    readonly List<GrowthObservation> _published = [];
    long _replacementCreated;
    long _copied;

    GrowthSession(FileIndex index, ConcurrentQueue<string> diagnostics, TimeProvider clock)
    {
        _index = index;
        _diagnostics = diagnostics;
        _clock = clock;
        index.Hooks.WatchGrowthStepReached = (_, step, _) => Observe(step);
    }

    /// <summary>The published block, read afresh: a growth publishes a replacement under the same root block.</summary>
    internal BlockFile Block => (_index.CurrentBlock ?? throw new InvalidOperationException("The growth root has no block.")).Block;

    /// <summary>Copies the block at <paramref name="blockPath" /> into <paramref name="workDirectory" /> and opens it there cache-only.</summary>
    /// <param name="blockPath">The completed synthetic block, left unchanged.</param>
    /// <param name="workDirectory">An empty directory: the cache directory of the open, and the volume's root directory.</param>
    /// <param name="clock">The clock batches and growth steps are timed with.</param>
    internal static async Task<GrowthSession> OpenAsync(string blockPath, string workDirectory, TimeProvider clock)
    {
        File.Copy(blockPath, Path.Combine(workDirectory, Path.GetFileName(blockPath)));
        var diagnostics = new ConcurrentQueue<string>();
        var index = await FileIndex.OpenAsync(BenchmarkRunner.CachedBlockOptions(GrowthSyntheticVolume.Volume,
            workDirectory, 0, ProducerKind.Mft) with
        {
            CacheDirectory = workDirectory,
            CacheMode = CacheMode.ExistingOnly,
            Diagnostics = diagnostics.Enqueue
        }, CancellationToken.None).ConfigureAwait(false);
        if (index.CurrentBlock is null)
        {
            var state = index.Status.State;
            await index.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"The synthetic growth block did not open: {state}.");
        }

        return new GrowthSession(index, diagnostics, clock);
    }

    /// <summary>Applies <paramref name="batches" /> in order, then waits for the last growth's background flush.</summary>
    /// <param name="batches">The journal batches, in USN order.</param>
    internal async Task<BurstResult> ApplyAsync(IEnumerable<UsnJournalEntry[]> batches)
    {
        var growthsBefore = _published.Count;
        var linesBefore = GrowthLines().Length;
        var batchTimes = new List<(double Milliseconds, int Growths)>();
        var changes = 0L;
        foreach (var batch in batches)
        {
            var before = _published.Count;
            var started = _clock.GetTimestamp();
            await _index.ApplyJournalEntriesAsync(GrowthSyntheticVolume.Volume, batch,
                GrowthSyntheticVolume.JournalIdentifier, batch[^1].Usn + 1).ConfigureAwait(false);
            batchTimes.Add((_clock.GetElapsedTime(started).TotalMilliseconds, _published.Count - before));
            changes += batch.Length;
        }

        await _index.GrowthFlushForTest(GrowthSyntheticVolume.Volume).ConfigureAwait(false);
        var block = Block;
        return new BurstResult(changes, _published.Skip(growthsBefore).ToArray(), GrowthLines()[linesBefore..],
            GrowthPauses(batchTimes), WatchGrowthPlan.CapacitiesOf(block), block.DeltaEntryCount,
            block.Header.IsCapacityExhausted);
    }

    public ValueTask DisposeAsync() => _index.DisposeAsync();

    /// <summary>Times a growth from its empty replacement to its copy and on to its publication, and measures what it wrote.</summary>
    void Observe(FileIndex.WatchGrowthStep step)
    {
        switch (step)
        {
            case FileIndex.WatchGrowthStep.ReplacementCreated:
                _replacementCreated = _clock.GetTimestamp();
                break;
            case FileIndex.WatchGrowthStep.Copied:
                _copied = _clock.GetTimestamp();
                break;
            case FileIndex.WatchGrowthStep.Published:
                var published = _clock.GetTimestamp();
                _published.Add(new GrowthObservation(GrowthCopyPhases.UsedBytes(Block),
                    _clock.GetElapsedTime(_replacementCreated, _copied).TotalMilliseconds,
                    _clock.GetElapsedTime(_copied, published).TotalMilliseconds));
                break;
        }
    }

    string[] GrowthLines() =>
        [.. _diagnostics.Where(line => line.StartsWith("Grew block file", StringComparison.Ordinal))];

    /// <summary>
    ///     Per batch that grew, the time it took beyond the median batch that did not grow, never negative; zero with no
    ///     plain batch to compare.
    /// </summary>
    internal static double[] GrowthPauses(IReadOnlyList<(double Milliseconds, int Growths)> batchTimes)
    {
        var plain = batchTimes.Where(batch => batch.Growths == 0).Select(batch => batch.Milliseconds).Order().ToArray();
        var median = plain.Length == 0 ? double.PositiveInfinity
            : plain.Length % 2 == 0 ? (plain[plain.Length / 2 - 1] + plain[plain.Length / 2]) / 2
            : plain[plain.Length / 2];
        return [.. batchTimes.Where(batch => batch.Growths != 0).Select(batch => Math.Max(0, batch.Milliseconds - median))];
    }
}

/// <summary>What one run of journal changes did to the synthetic block.</summary>
/// <param name="Changes">The journal records applied.</param>
/// <param name="Growths">Every growth, in order.</param>
/// <param name="GrowthLines">The library's diagnostics line for each growth, naming the refused region and any compaction.</param>
/// <param name="BatchPauses">Per batch that grew, its pause (<see cref="GrowthSession.GrowthPauses" />); a batch can hold more than one growth.</param>
/// <param name="Final">The capacities of the block at the end.</param>
/// <param name="FinalDeltaEntries">The delta entries the final block holds.</param>
/// <param name="CapacityExhausted">Whether any record was skipped because no growth could store it.</param>
sealed record BurstResult(long Changes, GrowthObservation[] Growths, string[] GrowthLines, double[] BatchPauses,
    BlockCapacities Final, uint FinalDeltaEntries, bool CapacityExhausted)
{
    /// <summary>Every growth's used bytes, summed.</summary>
    internal long BytesCopied => Growths.Sum(growth => growth.BytesWritten);

    /// <summary>Every growing batch's pause, summed.</summary>
    internal double PauseMilliseconds => BatchPauses.Sum();
}

/// <summary>One growth seen through the growth steps.</summary>
/// <param name="BytesWritten">The used bytes written into its replacement (<see cref="GrowthCopyPhases.UsedBytes" />).</param>
/// <param name="CopyMilliseconds">From the empty replacement to the completed copy: rows, names, header and posting build.</param>
/// <param name="PublishMilliseconds">From the completed copy to publication: the renames and the volatile store.</param>
sealed record GrowthObservation(long BytesWritten, double CopyMilliseconds, double PublishMilliseconds);
