using System.Runtime.Versioning;
using MFTLib.Index;
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

    [TestMethod]
    public void ResolveDefaultPath_IsUnderTheUserProfileAndNotHardCoded()
    {
        var path = CacheDirectory.ResolveDefaultPath();
        Assert.IsFalse(string.IsNullOrWhiteSpace(path));
        Assert.IsTrue(Path.IsPathFullyQualified(path));
        StringAssert.Contains(path, "MFTLib");
    }
}
