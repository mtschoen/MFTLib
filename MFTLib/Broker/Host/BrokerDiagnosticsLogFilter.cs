using MFTLib.Index;
using MFTLib.NativeInterop;

namespace MFTLib;

/// <summary>
///     While broker diagnostics are enabled, drops journal entries for the diagnostics log
///     files - the broker's own and the client's, whose path the client forwards with
///     <c>--diag-log</c> - before JournalBatch frames are built. Without this filter every
///     logged frame line produces a USN record for the log file, which ships as another
///     JournalBatch, which gets logged again: a self-sustaining loop that drowns the watched
///     volume's real traffic (file-wizard#299, git-wizard#143). Matching is by file reference
///     number (record number plus sequence number), which survives renames of the log file.
///     The filter is inactive when diagnostics are off or when the consumer opted the entries
///     back in with <c>MFTLIB_BROKER_DIAG_INCLUDE_SELF=1</c>. Configured log paths are re-checked
///     on each batch so that a recreated or replaced log file's new file reference is tracked
///     while retaining prior references for rename handling.
/// </summary>
sealed class BrokerDiagnosticsLogFilter
{
    readonly Func<string, ulong?> _resolveFileReference;

    const uint ShareAll = 0x7; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    const uint OpenExisting = 3;

    // Paths to resolve to file reference numbers. Resolution is evaluated on each
    // filtered batch, so a log file created after the watch armed or replaced mid-session
    // starts filtering from the next batch on while preserving rename handling for prior files.
    readonly List<string> _logPaths = [];

    // Watched drive -> references to drop.
    readonly Dictionary<DriveKey, HashSet<ulong>> _referencesByDrive = [];

    internal BrokerDiagnosticsLogFilter(string ownLogPath, string? clientLogPath,
        Func<string, ulong?>? resolveFileReference = null)
    {
        _resolveFileReference = resolveFileReference ?? ResolveFileReference;
        var normalizedOwn = NormalizePath(ownLogPath, OperatingSystem.IsWindows());
        _logPaths.Add(normalizedOwn);
        if (!string.IsNullOrEmpty(clientLogPath))
        {
            var normalizedClient = NormalizePath(clientLogPath, OperatingSystem.IsWindows());
            if (!string.Equals(normalizedClient, normalizedOwn, StringComparison.OrdinalIgnoreCase))
            {
                _logPaths.Add(normalizedClient);
            }
        }
    }

    internal UsnJournalEntry[] Filter(DriveKey drive, UsnJournalEntry[] entries)
    {
        ResolvePathsFor(drive);
        if (entries.Length == 0 ||
            !_referencesByDrive.TryGetValue(drive, out var references) ||
            references.Count == 0)
        {
            return entries;
        }

        var dropped = 0;
        foreach (var entry in entries)
        {
            if (references.Contains(FileReferenceOf(entry)))
            {
                dropped++;
            }
        }

        // No match: return the incoming array untouched, so a batch without log traffic
        // allocates nothing on this hot path.
        if (dropped == 0)
        {
            return entries;
        }

        var kept = new UsnJournalEntry[entries.Length - dropped];
        var index = 0;
        foreach (var entry in entries)
        {
            if (!references.Contains(FileReferenceOf(entry)))
            {
                kept[index++] = entry;
            }
        }

        return kept;
    }

    void ResolvePathsFor(DriveKey drive)
    {
        foreach (var path in _logPaths)
        {
            if (!TryGetDrive(path, out var pathDrive) || pathDrive != drive)
            {
                continue; // rooted on another drive; that drive's watch resolves it
            }

            if (_resolveFileReference(path) is not { } reference)
            {
                continue; // not created yet or unresolvable; retried on the next batch
            }

            if (!_referencesByDrive.TryGetValue(drive, out var set))
            {
                set = [];
                _referencesByDrive[drive] = set;
            }

            set.Add(reference);
        }
    }

    internal static ulong? ResolveFileReference(string path) => ResolveFileReference(path, OperatingSystem.IsWindows());

    internal static ulong? ResolveFileReference(string path, bool isWindows)
    {
        if (!isWindows)
        {
            return null;
        }

        // Desired access 0 still answers GetFileInformationByHandle; ShareAll keeps the
        // process appending to the log undisturbed.
        using var handle = Kernel32.Hooks.CreateFile(path, 0, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid || !Kernel32.GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
    }

    static ulong FileReferenceOf(UsnJournalEntry entry) =>
        ((ulong)entry.SequenceNumber << 48) | entry.RecordNumber;

    // Extract the bare drive from a rooted path ("C:\...", "C:...", or extended "\\?\C:\...");
    // anything else (UNC, non-Windows root) never matches a watched drive and stays pending.
    internal static bool TryGetDrive(string path, out DriveKey drive) =>
        TryGetDrive(path, OperatingSystem.IsWindows(), out drive);

    internal static bool TryGetDrive(string path, bool isWindows, out DriveKey drive)
    {
        drive = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Standard Windows drive-letter path: "C:\...", "C:/...", "C:file"
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
        {
            drive = new DriveKey(path[0]);
            return true;
        }

        // Extended-length or device path: "\\?\C:\...", "\\.\C:\...", "\??\C:\...", "//?/C:/...", "//./C:/..."
        if (path.Length >= 6 &&
            (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
             path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
             path.StartsWith(@"\??\", StringComparison.Ordinal) ||
             path.StartsWith("//?/", StringComparison.Ordinal) ||
             path.StartsWith("//./", StringComparison.Ordinal)) &&
            path[5] == ':' &&
            char.IsAsciiLetter(path[4]))
        {
            drive = new DriveKey(path[4]);
            return true;
        }

        if (isWindows)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (fullPath.Length >= 2 && fullPath[1] == ':' && char.IsAsciiLetter(fullPath[0]))
                {
                    drive = new DriveKey(fullPath[0]);
                    return true;
                }
            }
            catch
            {
                // Invalid path format
            }
        }

        return false;
    }

    internal static string NormalizePath(string path, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (isWindows)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        return path;
    }
}
