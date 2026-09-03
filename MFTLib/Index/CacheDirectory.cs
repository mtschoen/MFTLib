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
    public static string ResolveDefaultPath()
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
        return $"{char.ToUpperInvariant(driveLetter)}-{volumeSerial:X8}.mlix";
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
