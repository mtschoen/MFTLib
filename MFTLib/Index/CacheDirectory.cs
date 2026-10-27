using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MFTLib.Index;

/// <summary>
///     Where blocks live and how that directory is protected. The cache holds whole-volume file
///     metadata, so the directory is created with an explicit access-control list: owner and
///     SYSTEM only, inheritance blocked so no Administrators or Users entry is inherited. On
///     platforms without access-control lists the equivalent is owner-only Unix permissions.
/// </summary>
public static partial class CacheDirectory
{
    /// <summary>
    ///     The cached roots in <paramref name="cacheDirectoryPath" />, one record per file whose
    ///     name this class wrote. The listing is eager rather than lazy so a missing directory is
    ///     an empty result at the call rather than a deferred throw from the caller's own
    ///     <c>foreach</c>. Nothing is validated here, and only a directory-shaped file is opened (to read its
    ///     stored key); validation stays in the open path, which already reports per-root failures, and
    ///     every other field on the record comes from the directory entry the walk already read.
    ///     Rejected filenames are silently excluded; use the callback overload to observe them.
    /// </summary>
    internal static IReadOnlyList<CachedBlockFile> EnumerateCached(string cacheDirectoryPath)
        => EnumerateCached(cacheDirectoryPath, null);

    /// <summary>
    ///     The cached roots in <paramref name="cacheDirectoryPath" />, one record per file whose
    ///     name this class wrote. The listing is eager rather than lazy so a missing directory is
    ///     an empty result at the call rather than a deferred throw from the caller's own
    ///     <c>foreach</c>. Nothing is validated here, and only a directory-shaped file is opened (to read its
    ///     stored key); validation stays in the open path, which already reports per-root failures, and
    ///     every other field on the record comes from the directory entry the walk already read.
    /// </summary>
    /// <param name="cacheDirectoryPath">The cache directory to enumerate.</param>
    /// <param name="rejectedFile">
    ///     Optional synchronous callback for each non-canonical filename encountered by the
    ///     top-level block-file enumeration. Reports the full path and a human-readable reason;
    ///     no file contents are opened and no lock is created for the rejected entry. Only
    ///     canonical filenames enter the returned list. Ordering is unspecified. Exceptions propagate
    ///     immediately, before any canonical inspection or deletion in this call. Return promptly
    ///     and do not mutate the directory during enumeration.
    /// </param>
    internal static IReadOnlyList<CachedBlockFile> EnumerateCached(
        string cacheDirectoryPath, Action<CachedBlockRejection>? rejectedFile)
    {
        var cached = new List<CachedBlockFile>();
        foreach (var candidate in EnumerateCandidates(cacheDirectoryPath, rejectedFile, null))
        {
            var root = candidate.Root ?? ReadStoredRoot(candidate);
            if (root is null)
            {
                rejectedFile?.Invoke(new CachedBlockRejection(candidate.Path, "Unreadable block root key."));
                continue;
            }

            cached.Add(candidate.ToFile(root.Value));
        }

        return cached;
    }

    /// <summary>
    ///     The files whose name this class wrote, restricted to the stems of <paramref name="selected" /> when it is
    ///     not null. Nothing is opened: a candidate's root is the volume its name carries, or the selected root whose
    ///     stem its name carries, and is null only for an unselected directory-shaped name, whose key page is read
    ///     only after the caller holds the slot lock.
    /// </summary>
    static List<CacheCandidate> EnumerateCandidates(string cacheDirectoryPath,
        Action<CachedBlockRejection>? rejectedFile, Dictionary<string, RootKey>? selected)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDirectoryPath);
        var directory = new DirectoryInfo(cacheDirectoryPath);
        var candidates = new List<CacheCandidate>();
        if (!directory.Exists)
        {
            return candidates;
        }

        foreach (var file in directory.EnumerateFiles("*" + BlockFileExtension))
        {
            if (!TryParseBlockFileName(file.Name, out var parsed))
            {
                rejectedFile?.Invoke(new CachedBlockRejection(file.FullName, "Invalid block filename."));
                continue;
            }

            RootKey? root = parsed.Volume;
            if (selected is not null)
            {
                if (!selected.TryGetValue(parsed.Stem, out var selectedRoot))
                {
                    continue;
                }

                root ??= selectedRoot;
            }

            candidates.Add(new CacheCandidate(parsed, file.FullName, file.Length, file.LastWriteTimeUtc, root));
        }

        return candidates;
    }

    /// <summary>
    ///     Inspects selected cached blocks while holding each block's slot lock. A missing
    ///     directory returns an empty list. A lock that is held or cannot be opened reports
    ///     InUse without reading or validating block bytes (only an unselected directory-shaped name, which
    ///     carries just a hash, has its stored key read to identify it); an unreadable or rejected block reports
    ///     Invalid using the reason the block failed validation.
    ///     Inspection creates a persistent .lock sibling when absent and never unlinks it.
    ///     The block is disposed before its lock is released, after the root name has been
    ///     copied into a managed string. Results describe inspection time, not a reservation.
    ///     Rejected filenames are silently excluded unless <paramref name="rejectedFile" /> is supplied.
    /// </summary>
    /// <param name="cacheDirectoryPath">The cache directory to inspect.</param>
    /// <param name="roots">
    ///     Root keys to include, or null for all. The default key is rejected. Filtering happens before any
    ///     lock attempt, so excluded blocks have no lock file opened or created.
    /// </param>
    /// <param name="rejectedFile">
    ///     Optional synchronous callback for each non-canonical filename encountered by the
    ///     top-level block-file enumeration. Reports the full path and a human-readable reason;
    ///     no file contents are opened and no lock is created for the rejected entry. Reports
    ///     are not filtered by root selection. Ordering is unspecified. Exceptions propagate
    ///     immediately, before any canonical inspection or deletion in this call. Return promptly
    ///     and do not mutate the directory during enumeration.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="cacheDirectoryPath" /> is null or empty, or <paramref name="roots" /> holds the default key.</exception>
    public static IReadOnlyList<CachedBlockStatus> InspectCached(
        string cacheDirectoryPath, IEnumerable<RootKey>? roots = null,
        Action<CachedBlockRejection>? rejectedFile = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDirectoryPath);
        var selected = SelectedRoots(roots);
        var statuses = new List<CachedBlockStatus>();
        foreach (var candidate in EnumerateCandidates(cacheDirectoryPath, rejectedFile, selected))
        {
            using var owner = BlockOwnerLock.TryAcquire(candidate.Path);
            if (owner is null)
            {
                if (IdentifyContended(candidate, rejectedFile) is { } contended)
                {
                    statuses.Add(new CachedBlockStatus(contended, CachedBlockAvailability.InUse, null, null, null));
                }

                continue;
            }

            if (IdentifyHeld(candidate, rejectedFile) is { } file)
            {
                statuses.Add(InspectHeldFile(file));
            }
        }

        return statuses;
    }

    /// <summary>
    ///     Names a candidate whose slot lock another owner holds. A selected or volume-shaped candidate already has its
    ///     root. An unselected directory-shaped one has only a hash, so its key page is read to say which root is in
    ///     use; when that cannot be read the file is reported as rejected, since it has no identity to report.
    /// </summary>
    static CachedBlockFile? IdentifyContended(CacheCandidate candidate, Action<CachedBlockRejection>? rejectedFile)
    {
        var root = candidate.Root ?? ReadStoredRoot(candidate);
        if (root is null)
        {
            rejectedFile?.Invoke(new CachedBlockRejection(candidate.Path, "Unreadable block root key."));
            return null;
        }

        return candidate.ToFile(root.Value);
    }

    /// <summary>
    ///     Names a candidate whose slot lock this call holds: the stored key when it reads and derives the file name,
    ///     else the selected or volume root; an unselected directory-shaped file with no readable key is rejected.
    /// </summary>
    static CachedBlockFile? IdentifyHeld(CacheCandidate candidate, Action<CachedBlockRejection>? rejectedFile)
    {
        var root = (candidate.Parsed.Volume is null ? ReadStoredRoot(candidate) : null) ?? candidate.Root;
        if (root is null)
        {
            rejectedFile?.Invoke(new CachedBlockRejection(candidate.Path, "Unreadable block root key."));
            return null;
        }

        return candidate.ToFile(root.Value);
    }

    static RootKey? ReadStoredRoot(CacheCandidate candidate) =>
        BlockFile.TryReadStoredRootKey(candidate.Path, out var root) &&
        string.Equals(BlockFileName(root, candidate.Parsed.VolumeSerial), Path.GetFileName(candidate.Path),
            StringComparison.Ordinal)
            ? root
            : null;

    static CachedBlockStatus InspectHeldFile(CachedBlockFile file)
    {
        using var block = BlockFile.OpenCanonical(file.Path, file.VolumeSerial, out var validation);
        if (block is null)
        {
            return new CachedBlockStatus(file, CachedBlockAvailability.Invalid, validation, null, null);
        }

        var producerKind = block.Header.ProducerKind;
        var rootDirectory = producerKind switch
        {
            ProducerKind.Mft => $"{file.Root}:\\",
            ProducerKind.Enumeration when block.Header.RowCount > 0 =>
                NamePool.ReadRowName(block, 0).ToString(),
            _ => null
        };
        return new CachedBlockStatus(file, CachedBlockAvailability.Available,
            validation, producerKind, rootDirectory)
        {
            CacheTag = block.Header.CacheTag,
            ScanShape = block.Header.ScanShape
        };
    }

    /// <summary>
    ///     Eagerly deletes selected canonical cached blocks under their non-blocking slot locks.
    ///     Missing directories return an empty list. Unavailable locks report InUse; deletion
    ///     I/O and access failures report Failed with a reason, without stopping other attempts.
    ///     Lock files are created when needed and never deleted. Block contents are not validated.
    ///     Results describe each attempt, not a reservation against later cache creation.
    ///     Filenames this class did not write are silently excluded.
    ///     Each returned result carries the deletion outcome and a failure reason when applicable.
    /// </summary>
    /// <param name="cacheDirectoryPath">The cache directory to clear.</param>
    /// <param name="roots">Root keys to include, or null for all. The default key is rejected.</param>
    /// <remarks>
    ///     Deleted includes an inventory entry already removed before this attempt. Directory-level
    ///     enumeration lists only files whose name this class wrote. Filtering precedes lock acquisition. Retired siblings,
    ///     unrelated names and subdirectories are not candidates.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="cacheDirectoryPath" /> is null or empty, or <paramref name="roots" /> holds the default key.</exception>
    public static IReadOnlyList<CachedBlockDeletionResult> DeleteCached(
        string cacheDirectoryPath, IEnumerable<RootKey>? roots = null)
        => DeleteCached(cacheDirectoryPath, roots, null);

    /// <summary>
    ///     Eagerly deletes selected canonical cached blocks under their non-blocking slot locks.
    ///     Missing directories return an empty list. Unavailable locks report InUse; deletion
    ///     I/O and access failures report Failed with a reason, without stopping other attempts.
    ///     Lock files are created when needed and never deleted. Block contents are not validated.
    ///     Results describe each attempt, not a reservation against later cache creation.
    ///     Each returned result carries the deletion outcome and a failure reason when applicable.
    /// </summary>
    /// <param name="cacheDirectoryPath">The cache directory to clear.</param>
    /// <param name="roots">Root keys to include, or null for all. The default key is rejected.</param>
    /// <param name="rejectedFile">
    ///     Optional synchronous callback for each non-canonical filename encountered by the
    ///     top-level block-file enumeration. Reports the full path and a human-readable reason;
    ///     no file contents are opened and no lock is created for the rejected entry. Reports
    ///     are not filtered by root selection. Ordering is unspecified. Exceptions propagate
    ///     immediately, before any canonical inspection or deletion in this call. Return promptly
    ///     and do not mutate the directory during enumeration.
    /// </param>
    /// <remarks>
    ///     Deleted includes an inventory entry already removed before this attempt. Directory-level
    ///     enumeration follows EnumerateCached. Filtering precedes lock acquisition. Retired siblings,
    ///     unrelated names and subdirectories are not candidates.
    /// </remarks>
    internal static IReadOnlyList<CachedBlockDeletionResult> DeleteCached(
        string cacheDirectoryPath, IEnumerable<RootKey>? roots,
        Action<CachedBlockRejection>? rejectedFile)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDirectoryPath);
        var selected = SelectedRoots(roots);
        var results = new List<CachedBlockDeletionResult>();
        foreach (var candidate in EnumerateCandidates(cacheDirectoryPath, rejectedFile, selected))
        {
            if (DeleteCachedCandidate(candidate, rejectedFile) is { } result)
            {
                results.Add(result);
            }
        }

        return results;
    }

    internal static readonly DeletionHooks Hooks = new();

    /// <summary>Process-global test hooks for cache directory deletion.</summary>
    internal sealed class DeletionHooks
    {
        /// <summary>Invoked with the owner lock held, before File.Delete.</summary>
        internal Action<string>? _beforeDelete;

        internal void ResetToDefaults() => _beforeDelete = null;
    }

    static CachedBlockDeletionResult? DeleteCachedCandidate(CacheCandidate candidate,
        Action<CachedBlockRejection>? rejectedFile)
    {
        using var owner = BlockOwnerLock.TryAcquire(candidate.Path);
        if (owner is null)
        {
            return IdentifyContended(candidate, rejectedFile) is { } contended
                ? new CachedBlockDeletionResult(contended, CachedBlockDeletionOutcome.InUse, null)
                : null;
        }

        if (IdentifyHeld(candidate, rejectedFile) is not { } file)
        {
            return null;
        }

        Hooks._beforeDelete?.Invoke(file.Path);

        try
        {
            File.Delete(file.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CachedBlockDeletionResult(file, CachedBlockDeletionOutcome.Failed, exception.Message);
        }

        return new CachedBlockDeletionResult(file, CachedBlockDeletionOutcome.Deleted, null);
    }

    static Dictionary<string, RootKey>? SelectedRoots(IEnumerable<RootKey>? roots)
    {
        if (roots is null)
        {
            return null;
        }

        var selected = new Dictionary<string, RootKey>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            selected.TryAdd(NameStem(root.Validated()), root);
        }

        return selected;
    }

    /// <summary>
    ///     Creates the cache directory with its protection already in place, and re-applies that
    ///     protection to a directory that already exists. The second half matters as much as the
    ///     first: a directory the user created by hand, one an interrupted earlier run left
    ///     half-secured, or one restored from a backup keeps whatever permissions it carries, and
    ///     nothing else in the library ever re-checks it.
    /// </summary>
    /// <param name="path">The cache directory to create or secure.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is null or empty.</exception>
    public static DirectoryInfo EnsureCreated(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new DirectoryInfo(path);
        if (OperatingSystem.IsWindows())
        {
            return EnsureWindowsAccessControl(info);
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (!info.Exists)
        {
            return Directory.CreateDirectory(path, ownerOnly);
        }

        if (File.GetUnixFileMode(path) != ownerOnly)
        {
            File.SetUnixFileMode(path, ownerOnly);
        }

        return info;
    }

    [SupportedOSPlatform("windows")]
    static DirectoryInfo EnsureWindowsAccessControl(DirectoryInfo info)
    {
        if (!info.Exists)
        {
            // One call, so Windows attaches the descriptor as the directory comes into being.
            // Creating first and securing second leaves a window in which the directory carries
            // the parent's inherited entries, and a handle opened inside that window keeps the
            // access it was granted long after the descriptor is replaced.
            return BuildProtectedSecurity().CreateDirectory(info.FullName);
        }

        info.SetAccessControl(BuildProtectedSecurity());
        return info;
    }

    [SupportedOSPlatform("windows")]
    static DirectorySecurity BuildProtectedSecurity()
    {
        var security = new DirectorySecurity();

        // Protected and not inherited: an Administrators or Users entry from the parent would
        // otherwise let any local administrator read the whole volume's file metadata.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var identity in OwnerAndSystem())
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    [SupportedOSPlatform("windows")]
    static IEnumerable<IdentityReference> OwnerAndSystem()
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not null)
        {
            yield return identity.User;
        }
    }
}
