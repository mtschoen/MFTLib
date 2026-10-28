namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        enum CanonicalSlotOutcome
        {
            NotOwned,
            Owned,
            DeclinedInUse
        }

        CanonicalSlotOutcome ResolveCanonicalOwnership()
        {
            if (Options.CacheMode == CacheMode.Disabled)
            {
                return CanonicalSlotOutcome.NotOwned;
            }

            if (EnsureCanonicalOwnership())
            {
                // Cache-mode rescans can leave a renamed ".retired-*" sibling if a prior attempt
                // aborted before removing it; swept here, under the held owner lock.
                CleanupRetiredSiblings();
                return CanonicalSlotOutcome.Owned;
            }

            return !_settleWrites ? CanonicalSlotOutcome.DeclinedInUse : CanonicalSlotOutcome.NotOwned;
        }

        string CanonicalBlockPath() =>
            Path.Combine(creation.CacheDirectoryPath, CacheDirectory.BlockFileName(BlockRoot, Resolved.VolumeSerial));

        CacheLocation? DescribeCacheLocation(bool ownsCanonicalSlot) =>
            Options.CacheMode == CacheMode.Disabled
                ? null
                : ownsCanonicalSlot
                    ? CacheLocation.OwnedCanonical
                    : CacheLocation.PrivateFallback;

        /// <summary>
        ///     Takes the root's per-block owner lock if this index does not already hold it.
        ///     False means the lock could not be acquired because of contention or an access or I/O failure.
        /// </summary>
        bool EnsureCanonicalOwnership()
        {
            lock (StateLock)
            {
                if (_canonicalLock is not null)
                {
                    return true;
                }
            }

            var ownerLock = BlockOwnerLock.TryAcquire(CanonicalBlockPath());
            if (ownerLock is null)
            {
                return false;
            }

            lock (StateLock)
            {
                _canonicalLock = ownerLock;
            }

            return true;
        }

        /// <summary>The caller holds <see cref="StateLock" />.</summary>
        public void ReleaseCanonicalOwnershipLocked()
        {
            var owned = _canonicalLock;
            _canonicalLock = null;
            owned?.Dispose();
        }

        void CleanupRetiredSiblings()
        {
            var pattern = CacheDirectory.BlockFileName(BlockRoot, Resolved.VolumeSerial) + ".retired-*";
            try
            {
                foreach (var path in Hooks.EnumerateCacheFiles(creation.CacheDirectoryPath, pattern))
                {
                    TryDeleteBestEffort(path,
                        "sweeping a stale \".retired-*\" sibling left by a killed process");
                }
            }
            catch (IOException)
            {
                // Best effort: an inaccessible cache directory is reported by the warm-start
                // attempt that follows, not here.
            }
            catch (UnauthorizedAccessException)
            {
                // Same reasoning as the IOException case above.
            }
        }
    }
}
