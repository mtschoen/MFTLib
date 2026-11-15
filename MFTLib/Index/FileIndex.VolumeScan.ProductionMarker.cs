namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        /// <summary>
        ///     Set while a scan produces a replacement for this root's block, from the moment it renamed the canonical
        ///     file aside until it publishes, or until it restores the file and discards what it produced. Read and
        ///     written under <see cref="StateLock" />, and set with <see cref="WriteGate" /> held, so a
        ///     watch growth, which holds the write gate from its marker check to its publication, never runs while a
        ///     scan owns the canonical slot. A growth that meets the marker parks on it instead.
        /// </summary>
        TaskCompletionSource? _productionMarker;

        /// <summary>What a scan set up before producing: where it writes, and what it renamed aside.</summary>
        readonly record struct ProductionStart(ScanBlockTarget Target, RetiredCanonicalFile? Retired);

        /// <summary>
        ///     Takes the write gate briefly to find the block a scan supersedes, pick where it writes, rename the
        ///     canonical file aside, and set the production marker in one step, so no watch growth can be between its
        ///     marker check and its publication while the canonical file moves.
        /// </summary>
        /// <param name="superseded">
        ///     Picks the block the scan supersedes under the write gate; null when the root has none.
        /// </param>
        /// <param name="ownsCanonicalSlot">Picks whether the scan writes the canonical slot, under the write gate.</param>
        /// <param name="cancellationToken">Ends the wait for the write gate; nothing has moved yet then.</param>
        async Task<ProductionStart> BeginProductionAsync(Func<RootBlock?> superseded, Func<bool> ownsCanonicalSlot,
            CancellationToken cancellationToken)
        {
            await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var block = superseded();
                var target = ComputeScanTarget(ownsCanonicalSlot());
                var retired = RenameAsideForRescan(target, block);
                lock (StateLock)
                {
                    _productionMarker ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return new ProductionStart(target, retired);
            }
            finally
            {
                WriteGate.Release();
            }
        }

        /// <summary>
        ///     Ends a production that published nothing: restores or discards what the scan left
        ///     (<paramref name="cleanUp" />) and then clears the production marker, so a parked growth resumes only
        ///     once the canonical file is back. Unlike setting the marker, clearing it does not take the write gate: no
        ///     growth runs while the marker is set, so the restore cannot race one, and a scan cancelled at the
        ///     publication gate must clean up without waiting for that gate.
        /// </summary>
        void EndUnpublishedProduction(Action cleanUp)
        {
            try
            {
                cleanUp();
            }
            finally
            {
                lock (StateLock)
                {
                    ClearProductionMarkerLocked();
                }
            }
        }

        /// <summary>
        ///     Releases every growth parked on the production marker. The caller holds <see cref="StateLock" />;
        ///     continuations run asynchronously, so none runs under the lock.
        /// </summary>
        void ClearProductionMarkerLocked()
        {
            _productionMarker?.TrySetResult();
            _productionMarker = null;
        }

        /// <summary>The marker a growth parks on; false when no scan is producing.</summary>
        bool TryGetProductionMarker(out Task marker)
        {
            lock (StateLock)
            {
                marker = _productionMarker?.Task ?? Task.CompletedTask;
                return _productionMarker is not null;
            }
        }
    }
}
