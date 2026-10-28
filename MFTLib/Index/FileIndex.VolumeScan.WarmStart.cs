namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        /// <summary>
        ///     What opening a root's cached block found: the block to adopt, if any; why an existing
        ///     block was discarded; the journal checkpoint loss that rejected the block, or that a
        ///     cache-only open adopted it despite, which <see cref="CacheOnlyUnresumable" /> then says.
        /// </summary>
        readonly record struct WarmStartResult(
            BlockFile? Block,
            BlockValidationResult? DiscardedBlock,
            JournalCheckpointLoss? CheckpointLoss = null,
            bool CacheOnlyUnresumable = false);

        WarmStartResult TryOpenExistingBlock()
        {
            var path = CanonicalBlockPath();
            var existedBeforeOpen = File.Exists(path);

            // Ownership of a successfully opened block passes to the DriveBlock its adoption builds,
            // which releases it through its DriveBlockRelease state, not through IDisposable.
            if (BlockFile.OpenCanonical(path, Resolved.VolumeSerial, out var validation) is { } block)
            {
                block.Diagnostics = Options.Diagnostics;
                if (block.Header.ProducerKind != Resolved.Producer)
                {
                    var storedProducerKind = block.Header.ProducerKind;
                    block.Dispose();
                    DiscardRejectedBlock(path,
                        $"cache producer kind mismatch: stored {storedProducerKind}; requested {Resolved.Producer}");
                    return new WarmStartResult(null, null);
                }

                if (block.Header.CacheTag != Options.CacheTag)
                {
                    return RejectCacheTag(block, path);
                }

                if (block.Header.ScanShape != ExpectedScanShape)
                {
                    return RejectScanShape(block, path);
                }

                if (block.Header.ProducerKind == ProducerKind.Enumeration)
                {
                    string cachedRoot;
                    try
                    {
                        cachedRoot = NamePool.ReadRowName(block, 0).ToString();
                    }
                    catch (InvalidDataException)
                    {
                        block.Dispose();
                        DiscardRejectedBlock(path, $"cache validation failed: {BlockValidationResult.InvalidNameDescriptor}");
                        return new WarmStartResult(null, BlockValidationResult.InvalidNameDescriptor);
                    }

                    if (!NameMatching.EqualsName(cachedRoot, Resolved.RootDirectory, caseSensitive: !OperatingSystem.IsWindows()))
                    {
                        block.Dispose();
                        DiscardRejectedBlock(path, $"cache validation failed: {BlockValidationResult.WrongRootDirectory}");
                        return new WarmStartResult(null, BlockValidationResult.WrongRootDirectory);
                    }
                }

                return new WarmStartResult(block, null);
            }

            if (validation != BlockValidationResult.WrongMagic || existedBeforeOpen)
            {
                DiscardRejectedBlock(path, $"cache validation failed: {validation}");
            }

            // A block is only "discarded" when one genuinely existed and was rejected; a first-ever
            // scan with nothing at the path is not a discard.
            return new WarmStartResult(null, existedBeforeOpen ? validation : null);
        }

        /// <summary>
        ///     Drops a warm-start candidate whose journal checkpoint its source cannot safely adopt,
        ///     returning why in the result. Rejecting it here rescans once and keeps the reason,
        ///     rather than publishing stale rows or arming a watch from a lost position.
        ///     A watch-capable source checks retention and catches
        ///     up later; a scan-only source requires the journal's next USN to equal its stamped cursor.
        ///     Dump sources are never cached and never reach warm start. An enumeration block is also not
        ///     watched through the journal, so nothing about it can have fallen out of one.
        ///     <para>
        ///         <see cref="CacheMode.ExistingOnly" /> forbids the scan that would
        ///         otherwise follow a rejection, so the reasoning above does not apply to it: a
        ///         cache-only open never watches, and the block is still a correct snapshot as of
        ///         its age, so it is adopted anyway rather than failing the root. The result says so,
        ///         and the adoption marks the block unresumable on its runtime so a later
        ///         <see cref="StartWatchingAsync" /> refuses to start a watch from a cursor the
        ///         journal no longer holds.
        ///     </para>
        /// </summary>
        WarmStartResult RejectUnresumableCheckpoint(WarmStartResult warmStart)
        {
            if (warmStart.Block is not { } candidate || candidate.Header.ProducerKind != ProducerKind.Mft)
            {
                return warmStart;
            }

            var accepted = false;
            try
            {
                ref readonly var header = ref candidate.Header;
                var checkpointLoss = WatchSourceOrNull is null
                    ? JournalCheckpointCheck.CheckUnmoved(Volume, header.UsnJournalIdentifier, header.UsnNextUsn,
                        JournalCheckpointLossDetection.RootOpening)
                    : JournalCheckpointCheck.Check(Volume, header.UsnJournalIdentifier, header.UsnNextUsn,
                        JournalCheckpointLossDetection.RootOpening);
                if (checkpointLoss is not { } loss)
                {
                    accepted = true;
                    return warmStart;
                }

                if (Options.CacheMode == CacheMode.ExistingOnly)
                {
                    accepted = true;
                    return warmStart with { CheckpointLoss = loss, CacheOnlyUnresumable = true };
                }

                return new WarmStartResult(null, warmStart.DiscardedBlock, loss);
            }
            finally
            {
                if (!accepted)
                {
                    // An unadopted candidate has no DriveBlock; close its mapping directly.
                    candidate.Dispose();
                }
            }
        }

        WarmStartResult RejectCacheTag(BlockFile block, string path)
        {
            var stored = block.Header.CacheTag;
            block.Dispose();
            DiagnosticsReport.Send(Options.Diagnostics,
                $"Cache tag mismatch for '{path}': stored {stored}; requested {Options.CacheTag}.");
            DiscardRejectedBlock(path, $"cache validation failed: {BlockValidationResult.WrongCacheTag}");
            return new WarmStartResult(null, BlockValidationResult.WrongCacheTag);
        }

        /// <summary>
        ///     Deletes a rejected canonical block under the held owner lock, so a writing open's
        ///     cold scan replaces it. <see cref="CacheMode.ExistingOnly" /> never scans, so it has
        ///     nothing to replace the block with and never deletes it: the rejection is a refusal
        ///     and the file stays in place, whatever its contents, for a writing open to judge.
        /// </summary>
        void DiscardRejectedBlock(string path, string reason)
        {
            if (Options.CacheMode == CacheMode.ExistingOnly)
            {
                DiagnosticsReport.Send(Options.Diagnostics, $"Left block file '{path}' in place for a cache-only open: {reason}.");
                return;
            }

            TryDeleteBestEffort(path, reason);
        }

        /// <summary>
        ///     Rejects a block built under other row-shaping options. The caller holds the canonical slot's owner
        ///     lock, so a deletion cannot race another index adopting the file.
        /// </summary>
        WarmStartResult RejectScanShape(BlockFile block, string path)
        {
            var stored = block.Header.ScanShape;
            block.Dispose();
            DiagnosticsReport.Send(Options.Diagnostics, FormattableString.Invariant(
                $"Cache scan shape mismatch for '{path}': stored {stored:X16}; requested {ExpectedScanShape:X16}."));
            DiscardRejectedBlock(path, $"cache validation failed: {BlockValidationResult.WrongScanShape}");
            return new WarmStartResult(null, BlockValidationResult.WrongScanShape);
        }

        /// <summary>The scan shape this root's block must carry under the index's admitted options.</summary>
        ulong ExpectedScanShape => ScanShape.For(Resolved, Options);
    }
}
