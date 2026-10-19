namespace MFTLib.Index;

/// <summary>
///     How <see cref="FileIndex.OpenAsync" /> uses the block cache, as <see cref="FileIndexOptions.CacheMode" />.
/// </summary>
public enum CacheMode
{
    /// <summary>
    ///     Warm-starts each drive from the cache directory, scans what is missing and writes blocks
    ///     back. Requires <see cref="FileIndexOptions.CacheDirectory" />.
    /// </summary>
    Persistent,

    /// <summary>
    ///     Opens each drive from its existing cache block only and never scans. A drive with no
    ///     usable cache (missing, corrupt, or incompatible) is reported as <see cref="DriveState.Failed" />
    ///     with <see cref="DriveFailureKind.CacheDeclined" />, <see cref="DriveFailureKind.CacheIdentityMismatch" />
    ///     (or <see cref="DriveFailureKind.InUse" /> when the cache block's owner lock cannot be acquired)
    ///     instead of falling back to a scan, and <see cref="FileIndex.RescanAsync" />
    ///     remains available to scan it later. Having no scan to replace it with, this open never
    ///     deletes a rejected cache block: the file stays in place for a writing open to rebuild.
    ///     A cache block whose journal checkpoint the journal no longer holds is different: this
    ///     open never watches, and the block is still a correct snapshot as of its age, so it is
    ///     adopted instead of declined, with <see cref="DriveWatchStatus.CheckpointLoss" /> set to say
    ///     why the checkpoint could not be resumed. A later
    ///     <see cref="FileIndex.StartWatchingAsync" /> for such a drive returns a failed result carrying
    ///     <see cref="InvalidOperationException" /> (see <see cref="DriveWatchStatus.FailureMessage" />) and retains
    ///     the watch request (<see cref="DriveWatchStatus.IsRequested" />) until a stop withdraws it or a successful
    ///     <see cref="FileIndex.RescanAsync" /> gives the drive a fresh cursor and starts the watch.
    ///     Requires <see cref="FileIndexOptions.CacheDirectory" />.
    /// </summary>
    ExistingOnly,

    /// <summary>
    ///     Reads and writes no cache directory. Each block is created in the temp directory with
    ///     <see cref="FileOptions.DeleteOnClose" />, so the operating system removes the temp file
    ///     when the last handle closes. This includes process exit by kill rather than graceful
    ///     dispose, so no stale file is left behind. No cache directory is required, resolved or
    ///     created, even if one is supplied.
    /// </summary>
    Disabled
}
