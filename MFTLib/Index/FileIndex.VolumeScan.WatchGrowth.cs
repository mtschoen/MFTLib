using System.ComponentModel;

namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>The points of a watch growth a test can hold, or take a snapshot of the cache directory at.</summary>
    internal enum WatchGrowthStep
    {
        /// <summary>The empty replacement exists; nothing is copied yet.</summary>
        ReplacementCreated,

        /// <summary>The replacement is complete; the canonical file is still the published block.</summary>
        Copied,

        /// <summary>Between the two renames of a canonical growth: no file holds the canonical name.</summary>
        RenamedAside,

        /// <summary>The replacement holds the canonical name, or is complete in its private file; not published yet.</summary>
        RenamedIntoPlace,

        /// <summary>Every view reads the replacement, and the diagnostics line has been sent.</summary>
        Published,

        /// <summary>A canonical replacement's pages are written out by the background task, which has returned.</summary>
        Flushed
    }

    internal sealed partial class VolumeScan
    {
        Task _lastGrowthFlush = Task.CompletedTask;

        /// <summary>The background flush of the latest canonical growth, for a test to wait on.</summary>
        internal Task LastGrowthFlush => Volatile.Read(ref _lastGrowthFlush);

        /// <summary>
        ///     Steps 2 to 4 and 6 of a watch growth (docs/watch-lifetime.md, "Growth"): copies the published block of
        ///     <paramref name="rootBlock" /> into a replacement planned for <paramref name="refusal" />, renames it into
        ///     the canonical slot when this index owns it, publishes it as the lineage's current mapping, and flushes it
        ///     in the background. A compacting copy stores only the referenced names; nothing outside the block holds a
        ///     name identifier, so no reader observes the renumbering. The caller holds <see cref="WriteGate" />, not <see cref="StateLock" />, and found no
        ///     production marker. Until the renames begin, cancellation deletes the replacement and leaves the old
        ///     block published at the refused record's cursor; after them only disposal stops the growth.
        /// </summary>
        /// <param name="rootBlock">The root block the batch applies to; its lineage keeps its identity.</param>
        /// <param name="plan">The replacement's capacities and whether the copy compacts names, from <see cref="WatchGrowthPlan.Plan" />.</param>
        /// <param name="references">
        ///     The names the published block's rows reference, counted under the same write gate; its block is the copy
        ///     source, read once by the caller for the count, the plan and the copy.
        /// </param>
        /// <param name="refusal">The refusal the growth remedies, for the diagnostics line.</param>
        /// <param name="entry">The refused record, for the diagnostics line.</param>
        /// <param name="cancellationToken">The pump's stop token; the index's closing token is observed with it.</param>
        /// <exception cref="OperationCanceledException">Stopped or closing; the old block stays published.</exception>
        /// <exception cref="IOException">The replacement could not be created, written or renamed.</exception>
        /// <exception cref="UnauthorizedAccessException">The replacement could not be created or renamed.</exception>
        void GrowPublishedBlock(RootBlock rootBlock, PlannedGrowth plan, NameReferences references,
            JournalRefusal refusal, UsnJournalEntry entry, CancellationToken cancellationToken)
        {
            var source = references.Block;
            var capacities = plan.Capacities;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ClosingToken);
            var canonicalPath = OwnsCanonicalFileOf(source) ? CanonicalBlockPath() : null;
            var replacementPath = canonicalPath is null
                ? ComputeScanTarget(ownsCanonicalSlot: false).Path
                : $"{canonicalPath}.growing-{Guid.NewGuid():N}";
            // aislop-ignore-next-line dotnet/IDISP001 -- ownership moves to the lineage when the growth publishes it; every other exit disposes it
            var replacement = CreateGrowthReplacement(source, replacementPath, canonicalPath is null, capacities);
            var renamed = false;
            try
            {
                Hooks.WatchGrowthStepReached?.Invoke(Root, WatchGrowthStep.ReplacementCreated, cancellation.Token);
                if (plan.CompactsNames)
                {
                    BlockCopier.CopyCompacting(source, replacement, references, cancellation.Token);
                }
                else
                {
                    BlockCopier.Copy(source, replacement, buildPostings: true, complete: true, cancellation.Token);
                }

                Hooks.WatchGrowthStepReached?.Invoke(Root, WatchGrowthStep.Copied, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (canonicalPath is not null)
                {
                    RenameGrownBlockIntoPlace(rootBlock, replacement, replacementPath, canonicalPath, cancellation.Token);
                    renamed = true;
                }

                Hooks.WatchGrowthStepReached?.Invoke(Root, WatchGrowthStep.RenamedIntoPlace, cancellation.Token);
                PublishGrownBlock(rootBlock, replacement);
            }
            catch
            {
                // A renamed replacement already holds the canonical name at the refused record's cursor, the same rows as
                // the published block, so only its mapping goes; an unrenamed canonical replacement is deleted here.
                replacement.Dispose();
                if (canonicalPath is not null && !renamed)
                {
                    TryDeleteBestEffort(replacementPath, "discarding the replacement of a watch growth that did not finish");
                }

                throw;
            }

            ReportGrowth(rootBlock, refusal, entry, capacities, plan.CompactsNames ? references.Usage : null);
            Hooks.WatchGrowthStepReached?.Invoke(Root, WatchGrowthStep.Published, cancellation.Token);
            if (!replacement.DeletesOnClose)
            {
                Volatile.Write(ref _lastGrowthFlush, Task.Run(() => FlushGrownBlock(replacement), CancellationToken.None));
            }
        }

        /// <summary>
        ///     True when <paramref name="block" /> is the canonical file of a slot this index owns, the license to rename
        ///     it. A private or cache-disabled block grows into a new private delete-on-close file instead.
        /// </summary>
        bool OwnsCanonicalFileOf(BlockFile block)
        {
            lock (StateLock)
            {
                if (_canonicalLock is null)
                {
                    return false;
                }
            }

            return Options.CacheMode != CacheMode.Disabled && !block.DeletesOnClose &&
                   string.Equals(block.Path, CanonicalBlockPath(), StringComparison.Ordinal);
        }

        BlockFile CreateGrowthReplacement(BlockFile source, string path, bool deleteOnClose, BlockCapacities capacities)
        {
            var replacement = BlockFile.Create(new BlockFileCreateOptions
            {
                Path = path,
                Root = BlockRoot,
                VolumeSerial = source.Header.VolumeSerial,
                ProducerKind = source.Header.ProducerKind,
                RootRow = source.Header.RootRow,
                SlotCapacity = capacities.SlotCapacity,
                NameEntryCapacity = capacities.NameEntryCapacity,
                NamePoolCapacity = capacities.NamePoolCapacity,
                DeltaCapacity = capacities.DeltaCapacity,
                DeleteOnClose = deleteOnClose,
                Diagnostics = Diagnostics,
                CacheTag = source.Header.CacheTag,
                ScanShape = source.Header.ScanShape
            });
            replacement.Diagnostics = Diagnostics;
            return replacement;
        }

        /// <summary>
        ///     Step 3: renames the canonical file aside and schedules its deletion on the published mapping, then renames
        ///     the replacement to the canonical name. Two renames, because Windows refuses to move a file over a canonical
        ///     file that is still mapped (<c>BlockFileAtomicReplaceTests</c>). A failed second rename puts the old file
        ///     back, so the published block keeps its name.
        /// </summary>
        void RenameGrownBlockIntoPlace(RootBlock rootBlock, BlockFile replacement, string replacementPath,
            string canonicalPath, CancellationToken cancellationToken)
        {
            var retiredPath = $"{canonicalPath}.retired-{Guid.NewGuid():N}";
            File.Move(canonicalPath, retiredPath);
            try
            {
                Hooks.WatchGrowthStepReached?.Invoke(Root, WatchGrowthStep.RenamedAside, cancellationToken);
                File.Move(replacementPath, canonicalPath);
            }
            catch
            {
                TryRestoreRenamedAside(retiredPath, canonicalPath);
                throw;
            }

            rootBlock.ReleaseState.ScheduleDeleteAt(retiredPath, Diagnostics);
            replacement.AdoptRenamedPath(canonicalPath);
        }

        static void TryRestoreRenamedAside(string retiredPath, string canonicalPath)
        {
            try
            {
                File.Move(retiredPath, canonicalPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The published block stays mapped either way; with no canonical file the next open cold-scans and its
                // sweep removes the renamed-aside sibling.
            }
        }

        /// <summary>
        ///     Step 4: moves every view onto the replacement in one volatile store. Under <see cref="StateLock" />, so a
        ///     disposal that has begun closing refuses it and the old block stays published.
        /// </summary>
        void PublishGrownBlock(RootBlock rootBlock, BlockFile replacement)
        {
            lock (StateLock)
            {
                ThrowIfClosing(CancellationToken.None);
                rootBlock.Mapped.Replace(replacement);
            }
        }

        void ReportGrowth(RootBlock rootBlock, JournalRefusal refusal, UsnJournalEntry entry, BlockCapacities capacities,
            NameUsage? compacted)
        {
            var refused = FormattableString.Invariant(
                $"the journal record at USN {entry.Usn} (record {entry.RecordNumber}) was refused by the {refusal.Reason} region");
            if (compacted is { } names)
            {
                refused += FormattableString.Invariant(
                    $"; names compacted from {names.StoredEntries} to {names.ReferencedEntries} and pool bytes from {names.StoredPoolBytes} to {names.CompactedPoolBytes}");
            }

            var grown = FormattableString.Invariant(
                $"{capacities.SlotCapacity} slots, {capacities.NameEntryCapacity} name entries, {capacities.NamePoolCapacity} name-pool bytes and {capacities.DeltaCapacity} delta entries");
            var retained = FormattableString.Invariant($"{rootBlock.Mapped.RetiredBytes}");
            DiagnosticsReport.Send(Diagnostics,
                $"Grew block file '{rootBlock.Block.Path}': {refused}; the watch continues with {grown}. " +
                $"Retired mappings hold {retained} bytes until the root is rescanned or closed.");
        }

        /// <summary>
        ///     Step 6: writes a canonical replacement's pages out under an access scope, off the pump. A process kill loses
        ///     nothing without it, because mapped pages live in the shared page cache; this bounds what a power loss costs.
        ///     A replacement already being disposed is skipped, and a failed flush is reported, never thrown.
        /// </summary>
        void FlushGrownBlock(BlockFile replacement)
        {
            if (!replacement.TryTakeAccess())
            {
                return;
            }

            try
            {
                replacement.FlushUnderHeldAccess(null);
            }
            catch (Exception exception) when (exception is IOException or Win32Exception)
            {
                DiagnosticsReport.Send(Diagnostics,
                    $"Could not flush grown block file '{replacement.Path}': {exception.Message}");
            }
            finally
            {
                replacement.ReturnAccess();
                Hooks.WatchGrowthStepReached?.Invoke(Root, WatchGrowthStep.Flushed, CancellationToken.None);
            }
        }
    }
}
