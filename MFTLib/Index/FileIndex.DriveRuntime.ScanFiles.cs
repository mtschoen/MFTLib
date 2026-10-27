namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        /// <summary>
        ///     Where one drive's scan writes. <see cref="ScanBlockTarget.OwnsCanonicalSlot" /> is
        ///     the license every rename or delete of the canonical file checks: without it the
        ///     canonical file is not owned by this index and is never validated, renamed, or
        ///     deleted, and the scan goes to a private delete-on-close temp file instead.
        /// </summary>
        readonly record struct ScanBlockTarget(string Path, bool DeleteOnClose, bool OwnsCanonicalSlot,
            bool ExistedBeforeScan = false);

        /// <summary>A canonical cache file renamed aside so a scan can write the drive's new block in its place.</summary>
        readonly record struct RetiredCanonicalFile(string RetiredPath, string CanonicalPath, DriveBlock Superseded);

        ScanBlockTarget ComputeScanTarget(bool ownsCanonicalSlot)
        {
            if (Options.CacheMode == CacheMode.Disabled)
            {
                return new ScanBlockTarget(Path.Combine(Path.GetTempPath(),
                    $"mftlib-cache-disabled-{Guid.NewGuid():N}-{CacheDirectory.BlockFileName(Drive, Configuration.VolumeSerial)}"),
                    DeleteOnClose: true, OwnsCanonicalSlot: false);
            }

            if (ownsCanonicalSlot)
            {
                var canonicalPath = CanonicalBlockPath();
                return new ScanBlockTarget(canonicalPath, DeleteOnClose: false, OwnsCanonicalSlot: true,
                    ExistedBeforeScan: File.Exists(canonicalPath));
            }

            return new ScanBlockTarget(Path.Combine(Path.GetTempPath(),
                $"mftlib-private-{Guid.NewGuid():N}-{CacheDirectory.BlockFileName(Drive, Configuration.VolumeSerial)}"),
                DeleteOnClose: true, OwnsCanonicalSlot: false);
        }

        /// <summary>
        ///     Renames the file at <paramref name="target" />'s canonical path aside, when the scan
        ///     writes there, <paramref name="superseded" /> is the block that file backs, and a file
        ///     exists, and schedules it for deletion once <paramref name="superseded" /> is fully
        ///     released. Null when nothing was renamed.
        /// </summary>
        RetiredCanonicalFile? RenameAsideForRescan(ScanBlockTarget target, DriveBlock? superseded)
        {
            if (superseded is null || !target.OwnsCanonicalSlot || !File.Exists(target.Path))
            {
                return null;
            }

            var retiredPath = $"{target.Path}.retired-{Guid.NewGuid():N}";
            File.Move(target.Path, retiredPath);
            superseded.ReleaseState.ScheduleDeleteAt(retiredPath, Options.Diagnostics);
            return new RetiredCanonicalFile(retiredPath, target.Path, superseded);
        }

        /// <summary>
        ///     Cleans up after production into <paramref name="target" /> ended without a block (failed
        ///     or cancelled). A renamed-aside file is restored, which also deletes the partial
        ///     replacement. With nothing renamed, a canonical target's file is deleted here, while
        ///     this index still holds the slot's owner lock; a private target deletes itself on close.
        ///     A file that sat in the slot before the scan (a complete block whose checkpoint the journal
        ///     no longer holds) is judged by what is there now, not by who wrote it: it stays only while
        ///     it is still a complete, valid block, and a truncated or half-written replacement goes.
        /// </summary>
        void DiscardUnproducedTarget(ScanBlockTarget target, RetiredCanonicalFile? retired)
        {
            if (retired is not null)
            {
                RestoreRetiredFile(retired);
            }
            else if (target.OwnsCanonicalSlot &&
                     !(target.ExistedBeforeScan && HoldsCompleteBlock(target.Path, Configuration.VolumeSerial)))
            {
                DeletePartialCanonicalBlock(target.Path);
            }
        }

        /// <summary>
        ///     True when the file at <paramref name="path" /> still opens as a complete block for the
        ///     volume, the same validation the open path applies before it adopts a cached block.
        /// </summary>
        static bool HoldsCompleteBlock(string path, uint volumeSerial)
        {
            // Open reports an unreadable or invalid file as null rather than throwing.
            using var block = BlockFile.OpenCanonical(path, volumeSerial, out _);
            return block is not null;
        }

        /// <summary>
        ///     Deletes the partial block a failed or cancelled scan left at the canonical path and reports
        ///     the delete, or the failure to delete, through <see cref="FileIndexOptions.Diagnostics" />.
        ///     A failure here never replaces the scan's own exception: the next open rejects whatever
        ///     is left as incomplete.
        /// </summary>
        void DeletePartialCanonicalBlock(string path)
        {
            string report;
            try
            {
                var existed = File.Exists(path);
                File.Delete(path);
                if (!existed)
                {
                    return;
                }

                report =
                    $"Deleted block file '{path}': removing the partial block a failed or cancelled scan left in the canonical cache slot.";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                report =
                    $"Could not delete block file '{path}': the partial block a failed or cancelled scan left in the canonical cache slot ({exception.Message}).";
            }

            DiagnosticsReport.Send(Options.Diagnostics, report);
        }

        /// <summary>
        ///     Undoes <see cref="RenameAsideForRescan" /> when the scan fails or is cancelled. Nothing to
        ///     do when nothing was renamed.
        /// </summary>
        void RestoreRetiredFile(RetiredCanonicalFile? retired)
        {
            if (retired is not { } renamed)
            {
                return;
            }

            var (retiredPath, canonicalPath, superseded) = renamed;
            try
            {
                if (File.Exists(retiredPath))
                {
                    if (File.Exists(canonicalPath))
                    {
                        File.Delete(canonicalPath);
                        DiagnosticsReport.Send(Options.Diagnostics,
                            $"Deleted block file '{canonicalPath}': removing the partial replacement a failed or cancelled scan left before restoring the renamed-aside cache file.");
                    }

                    File.Move(retiredPath, canonicalPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                superseded.ReleaseState.ClearScheduledDelete();
            }
        }

        void TryDeleteBestEffort(string path, string reason)
        {
            try
            {
                File.Delete(path);
                DiagnosticsReport.Send(Options.Diagnostics, $"Deleted block file '{path}': {reason}.");
            }
            catch (IOException)
            {
                // Whatever still needs this file surfaces its own error; a leftover here is either
                // rejected again next time or, for a leftover still in use, simply left alone.
            }
            catch (UnauthorizedAccessException)
            {
                // Same reasoning as the IOException case above.
            }
        }
    }
}
