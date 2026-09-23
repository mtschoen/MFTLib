using System.Globalization;
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
public static class CacheDirectory
{
    static int _defaultCacheDirectoryForbidden;

    /// <summary>Returns the per-user default index cache directory path.</summary>
    /// <exception cref="InvalidOperationException">
    ///     Default-cache resolution was forbidden by the test host through
    ///     <c>CacheDirectoryIsolation.ForbidDefaultCacheDirectory</c>.
    ///     Set <see cref="FileIndexOptions.CacheDirectory" /> to a temporary path.
    /// </exception>
    public static string ResolveDefaultPath()
    {
        if (Volatile.Read(ref _defaultCacheDirectoryForbidden) != 0)
        {
            throw new InvalidOperationException(
                "CacheDirectoryIsolation.ForbidDefaultCacheDirectory has forbidden default cache resolution. " +
                "Set FileIndexOptions.CacheDirectory to a temporary path.");
        }

        return ComputeDefaultPath();
    }

    internal static void ForbidDefaultCacheDirectory()
    {
        Interlocked.Exchange(ref _defaultCacheDirectoryForbidden, 1);
    }

    static string ComputeDefaultPath()
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(applicationData))
        {
            applicationData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }

        return Path.Combine(applicationData, "MFTLib", "index");
    }

    public static string BlockFileName(char driveLetter, uint volumeSerial)
    {
        return $"{char.ToUpperInvariant(driveLetter)}-{volumeSerial:X8}{BlockFileExtension}";
    }

    /// <summary>
    ///     The cached drives in <paramref name="cacheDirectoryPath" />, one record per file whose
    ///     name this class wrote. The listing is eager rather than lazy so a missing directory is
    ///     an empty result at the call rather than a deferred throw from the caller's own
    ///     <c>foreach</c>. Nothing is opened or validated here; validation stays in the open path,
    ///     which already reports per-drive failures, and every field on the record comes from the
    ///     directory entry the walk already read, so no per-file stat can fail mid-listing.
    /// </summary>
    public static IReadOnlyList<CachedBlockFile> EnumerateCached(string cacheDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDirectoryPath);
        var directory = new DirectoryInfo(cacheDirectoryPath);
        if (!directory.Exists)
        {
            return [];
        }

        var cached = new List<CachedBlockFile>();
        foreach (var file in directory.EnumerateFiles("*" + BlockFileExtension))
        {
            if (!TryParseBlockFileName(file.Name, out var driveLetter, out var volumeSerial))
            {
                continue;
            }

            cached.Add(new CachedBlockFile(driveLetter, volumeSerial, file.FullName, file.Length,
                file.LastWriteTimeUtc));
        }

        return cached;
    }

    /// <summary>
    ///     Inspects selected cached blocks while holding each block's slot lock. A missing
    ///     directory returns an empty list. A lock that is held or cannot be opened reports
    ///     InUse without reading any block bytes; an unreadable or rejected block reports
    ///     Invalid using the reason returned by BlockFile.Open.
    ///     Inspection creates a persistent .lock sibling when absent and never unlinks it.
    ///     The block is disposed before its lock is released, after the root name has been
    ///     copied into a managed string. Results describe inspection time, not a reservation.
    /// </summary>
    /// <param name="cacheDirectoryPath">The cache directory to inspect.</param>
    /// <param name="driveLetters">
    ///     Uppercase drive letters to include, or null for all. Filtering happens before any
    ///     lock attempt, so excluded blocks have no lock file opened or created.
    /// </param>
    public static IReadOnlyList<CachedBlockStatus> InspectCached(
        string cacheDirectoryPath, IReadOnlySet<char>? driveLetters = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDirectoryPath);
        var statuses = new List<CachedBlockStatus>();
        foreach (var file in EnumerateCached(cacheDirectoryPath))
        {
            if (driveLetters is not null && !driveLetters.Contains(file.DriveLetter))
            {
                continue;
            }

            statuses.Add(InspectCachedFile(file));
        }

        return statuses;
    }

    static CachedBlockStatus InspectCachedFile(CachedBlockFile file)
    {
        using var owner = BlockOwnerLock.TryAcquire(file.Path);
        if (owner is null)
        {
            return new CachedBlockStatus(file, CachedBlockAvailability.InUse, null, null, null);
        }

        using var block = BlockFile.Open(file.Path, file.VolumeSerial, out var validation);
        if (block is null)
        {
            return new CachedBlockStatus(file, CachedBlockAvailability.Invalid, validation, null, null);
        }

        var producerKind = block.Header.ProducerKind;
        var rootDirectory = producerKind switch
        {
            ProducerKind.Mft => $"{file.DriveLetter}:\\",
            ProducerKind.Enumeration when block.Header.RowCount > 0 =>
                NamePool.ReadRowName(block, 0).ToString(),
            _ => null
        };
        return new CachedBlockStatus(file, CachedBlockAvailability.Available,
            validation, producerKind, rootDirectory)
        {
            CacheTag = block.Header.CacheTag
        };
    }

    const string BlockFileExtension = ".mlix";

    /// <summary>
    ///     The exact inverse of <see cref="BlockFileName" />, and private on purpose: the format
    ///     is the library's, not the consumer's. The final round trip through
    ///     <see cref="BlockFileName" /> is what keeps the two from ever drifting apart, so a name
    ///     is only accepted when the formatter would have produced exactly it.
    /// </summary>
    static bool TryParseBlockFileName(string fileName, out char driveLetter, out uint volumeSerial)
    {
        driveLetter = '\0';
        volumeSerial = 0;

        const int serialDigits = 8;
        var expectedLength = 1 + 1 + serialDigits + BlockFileExtension.Length;
        if (fileName.Length != expectedLength || fileName[1] != '-' || !char.IsAsciiLetter(fileName[0]))
        {
            return false;
        }

        if (!uint.TryParse(fileName.AsSpan(2, serialDigits), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var parsedSerial))
        {
            return false;
        }

        var candidateLetter = fileName[0];
        if (!string.Equals(BlockFileName(candidateLetter, parsedSerial), fileName, StringComparison.Ordinal))
        {
            return false;
        }

        driveLetter = candidateLetter;
        volumeSerial = parsedSerial;
        return true;
    }

    /// <summary>
    ///     Creates the cache directory with its protection already in place, and re-applies that
    ///     protection to a directory that already exists. The second half matters as much as the
    ///     first: a directory the user created by hand, one an interrupted earlier run left
    ///     half-secured, or one restored from a backup keeps whatever permissions it carries, and
    ///     nothing else in the library ever re-checks it.
    /// </summary>
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

        FileSystemAclExtensions.SetAccessControl(info, BuildProtectedSecurity());
        return info;
    }

    [SupportedOSPlatform("windows")]
    static DirectorySecurity BuildProtectedSecurity()
    {
        var security = new DirectorySecurity();

        // Protected and not inherited: an Administrators or Users entry from the parent would
        // otherwise let any local administrator read the whole volume's file metadata.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
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
