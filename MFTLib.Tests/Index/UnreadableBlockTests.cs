using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A cache block file the process is not allowed to open is "no usable block here" exactly
///     like a corrupt one: <see cref="FileIndex.OpenAsync" /> must discard it and cold-scan the
///     drive rather than letting the permission failure escape. A block left read-protected by a
///     backup tool, by another user's ownership, or by a profile copy would otherwise make every
///     later open of that drive throw permanently.
/// </summary>
[TestClass]
public class UnreadableBlockTests
{
    string _treeRoot = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in new[] { _treeRoot, _cacheDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows.
            }
            catch (UnauthorizedAccessException)
            {
                // A block left read-protected by a test that could not restore it.
            }
        }
    }

    FileIndexOptions Options()
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory
        };
    }

    [TestMethod]
    public async Task OpenAsync_UnreadableBlockFile_IsDiscardedAndTheDriveIsColdScanned()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));
        Assert.IsTrue(File.Exists(blockPath), "the first open must leave a warm-start block behind");

        DenyReadForCurrentUser(blockPath);
        try
        {
            if (CanOpenForReadWrite(blockPath))
            {
                Assert.Inconclusive(
                    "This account still opens the block file after the deny rule was applied, so an "
                    + "unprivileged test cannot create the unreadable condition on this platform.");
                return;
            }

            await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);

            Assert.AreEqual(BlockValidationResult.WrongMagic, reopened.Drives[0].DiscardedBlock);
            Assert.AreEqual(DriveState.Ready, reopened.Drives[0].State);
            Assert.IsTrue(reopened.Drives[0].RowCount >= 3,
                "the drive must be cold-scanned rather than left empty");
        }
        finally
        {
            RestoreReadAccess(blockPath);
        }
    }

    /// <summary>
    ///     Makes <paramref name="path" /> unopenable by this process: a deny-read access-control
    ///     entry for the current user on Windows, mode 000 elsewhere. Both are best effort, since
    ///     a privileged account bypasses either; the caller confirms the condition actually took
    ///     with <see cref="CanOpenForReadWrite" />.
    /// </summary>
    static void DenyReadForCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            AddWindowsDenyReadRule(path);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
    }

    static void RestoreReadAccess(string path)
    {
        try
        {
            // A cold scan recreates the block at the same canonical path, and that new file was
            // never restricted. Only a file this test can still not open needs undoing.
            if (!File.Exists(path) || CanOpenForReadWrite(path))
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                RemoveWindowsDenyReadRule(path);
                return;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (UnauthorizedAccessException)
        {
            // Nothing further this test can do; [TestCleanup] tolerates the leftover.
        }
        catch (IOException)
        {
            // Same reasoning as the UnauthorizedAccessException case above.
        }
    }

    static bool CanOpenForReadWrite(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    static void AddWindowsDenyReadRule(string path)
    {
        var info = new FileInfo(path);
        var security = FileSystemAclExtensions.GetAccessControl(info);
        security.AddAccessRule(CurrentUserDenyReadRule());
        FileSystemAclExtensions.SetAccessControl(info, security);
    }

    [SupportedOSPlatform("windows")]
    static void RemoveWindowsDenyReadRule(string path)
    {
        var info = new FileInfo(path);
        var security = FileSystemAclExtensions.GetAccessControl(info);
        security.RemoveAccessRule(CurrentUserDenyReadRule());
        FileSystemAclExtensions.SetAccessControl(info, security);
    }

    [SupportedOSPlatform("windows")]
    static FileSystemAccessRule CurrentUserDenyReadRule()
    {
        using var identity = WindowsIdentity.GetCurrent();
        Assert.IsNotNull(identity.User, "the current Windows identity must carry a user security identifier");
        return new FileSystemAccessRule(identity.User, FileSystemRights.Read, AccessControlType.Deny);
    }
}
