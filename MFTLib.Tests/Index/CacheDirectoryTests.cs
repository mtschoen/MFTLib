using System.Reflection;
using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class CacheDirectoryTests
{
    string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void EnsureCreated_CreatesTheDirectory()
    {
        var info = CacheDirectory.EnsureCreated(_root);
        Assert.IsTrue(Directory.Exists(_root));
        Assert.AreEqual(_root, info.FullName.TrimEnd(Path.DirectorySeparatorChar));
    }

    [TestMethod]
    public void EnsureCreated_IsIdempotent()
    {
        CacheDirectory.EnsureCreated(_root);
        CacheDirectory.EnsureCreated(_root);
        Assert.IsTrue(Directory.Exists(_root));
    }

    [TestMethod]
    public void EnsureCreated_OnUnixLeavesOwnerOnlyPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file mode is not meaningful on Windows.");
            return;
        }

        CacheDirectory.EnsureCreated(_root);
        var mode = File.GetUnixFileMode(_root);
        Assert.AreEqual(UnixFileMode.None, mode & UnixFileMode.GroupRead);
        Assert.AreEqual(UnixFileMode.None, mode & UnixFileMode.OtherRead);
    }

    [TestMethod]
    public void EnsureCreated_OnUnixNarrowsAnAlreadyWideDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file mode is not meaningful on Windows.");
            return;
        }

        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        CacheDirectory.EnsureCreated(_root);

        var mode = File.GetUnixFileMode(_root);
        Assert.AreEqual(UnixFileMode.None, mode & UnixFileMode.GroupRead);
        Assert.AreEqual(UnixFileMode.None, mode & UnixFileMode.GroupExecute);
        Assert.AreEqual(UnixFileMode.None, mode & UnixFileMode.OtherRead);
        Assert.AreEqual(UnixFileMode.None, mode & UnixFileMode.OtherExecute);
        Assert.AreEqual(UnixFileMode.UserRead, mode & UnixFileMode.UserRead);
    }

    [TestMethod]
    public void EnsureCreated_OnUnixWidensAnUnderpermissionedDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file mode is not meaningful on Windows.");
            return;
        }

        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(_root, UnixFileMode.None);

        CacheDirectory.EnsureCreated(_root);

        var mode = File.GetUnixFileMode(_root);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
    }

    [TestMethod]
    public void EnsureCreated_OnWindowsProtectsTheAccessRulesOfANewDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Access-control lists are a Windows concept.");
            return;
        }

        CacheDirectory.EnsureCreated(_root);

        Assert.IsTrue(AreAccessRulesProtected(_root),
            "a newly created cache directory must block inherited access-control entries");
    }

    [TestMethod]
    public void EnsureCreated_OnWindowsReprotectsADirectoryThatAlreadyExists()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Access-control lists are a Windows concept.");
            return;
        }

        // Stands in for a directory the user created by hand, or one an interrupted earlier run
        // created but never got to secure: it exists and it inherits the parent's entries.
        Directory.CreateDirectory(_root);
        Assert.IsFalse(AreAccessRulesProtected(_root), "the plain directory should start out inheriting");

        CacheDirectory.EnsureCreated(_root);

        Assert.IsTrue(AreAccessRulesProtected(_root));
    }

    [SupportedOSPlatform("windows")]
    static bool AreAccessRulesProtected(string path)
    {
        return FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path)).AreAccessRulesProtected;
    }

    [TestMethod]
    public void BlockFileName_CombinesDriveLetterAndSerial()
    {
        Assert.AreEqual("C-0BADF00D.mlix", CacheDirectory.BlockFileName('C', 0x0BADF00D));
        Assert.AreEqual("C-0BADF00D.mlix", CacheDirectory.BlockFileName('c', 0x0BADF00D));
    }

    static string ComputeDefaultPathForTest()
    {
        var method = typeof(CacheDirectory).GetMethod("ComputeDefaultPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return (string)method.Invoke(null, null)!;
    }

    [TestMethod]
    public void ComputeDefaultPath_IsUnderTheUserProfileAndNotHardCoded()
    {
        var path = ComputeDefaultPathForTest();
        Assert.IsFalse(string.IsNullOrWhiteSpace(path));
        Assert.IsTrue(Path.IsPathFullyQualified(path));
        StringAssert.Contains(path, "MFTLib");
    }

    [TestMethod]
    public void ResolveDefaultPath_InGuardedProcess_ThrowsWithIsolationInstructions()
    {
        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => CacheDirectory.ResolveDefaultPath());
        StringAssert.Contains(exception.Message,
            "CacheDirectoryIsolation.ForbidDefaultCacheDirectory");
        StringAssert.Contains(exception.Message, "FileIndexOptions.CacheDirectory");
        StringAssert.Contains(exception.Message, "temporary path");
    }

    [TestMethod]
    public void ForbidDefaultCacheDirectory_RepeatedCalls_KeepResolutionForbidden()
    {
        CacheDirectoryIsolation.ForbidDefaultCacheDirectory();
        CacheDirectoryIsolation.ForbidDefaultCacheDirectory();
        Assert.ThrowsException<InvalidOperationException>(
            () => CacheDirectory.ResolveDefaultPath());
    }

    [TestMethod]
    public async Task OpenAsync_OmittedCacheDirectory_ThrowsBeforeDirectoryCreation()
    {
        // Stop here if isolation regresses; never open an unguarded default cache.
        var guardException = Assert.ThrowsException<InvalidOperationException>(
            () => CacheDirectory.ResolveDefaultPath());
        var defaultPath = ComputeDefaultPathForTest();
        var existedBefore = Directory.Exists(defaultPath);

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await using var index = await FileIndex.OpenAsync(
                new FileIndexOptions { CacheDirectory = null, Drives = [] },
                CancellationToken.None);
        });

        Assert.AreEqual(guardException.Message, exception.Message);
        StringAssert.Contains(exception.Message, "FileIndexOptions.CacheDirectory");
        Assert.AreEqual(existedBefore, Directory.Exists(defaultPath));
    }

    [TestMethod]
    public async Task OpenAsync_ExplicitTemporaryCacheDirectory_StillCreatesDirectory()
    {
        Assert.IsFalse(Directory.Exists(_root));
        await using var index = await FileIndex.OpenAsync(
            new FileIndexOptions { CacheDirectory = _root, Drives = [] },
            CancellationToken.None);

        Assert.AreEqual(_root, index.CacheDirectoryPath);
        Assert.IsTrue(Directory.Exists(_root));
        Assert.AreEqual(0, index.Drives.Count);
    }
}
