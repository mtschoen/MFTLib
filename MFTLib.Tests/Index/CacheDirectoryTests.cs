using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class CacheDirectoryTests : IDisposable
{
    TempDirectory _rootOwner = null!;
    string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _rootOwner = new TempDirectory("mftlib-cache", create: false);
        _root = _rootOwner.Path;
    }

    [TestCleanup]
    public void Cleanup() => _rootOwner.Dispose();

    public void Dispose() => Cleanup();

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
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [UnsupportedOSPlatform("windows")]
    public void EnsureCreated_OnUnixLeavesOwnerOnlyPermissions()
    {
        CacheDirectory.EnsureCreated(_root);
        var mode = File.GetUnixFileMode(_root);
        Assert.AreEqual(UnixFileMode.None, mode & UnixFileMode.GroupRead);
        Assert.AreEqual(UnixFileMode.None, mode & UnixFileMode.OtherRead);
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [UnsupportedOSPlatform("windows")]
    public void EnsureCreated_OnUnixNarrowsAnAlreadyWideDirectory()
    {
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
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [UnsupportedOSPlatform("windows")]
    public void EnsureCreated_OnUnixWidensAnUnderpermissionedDirectory()
    {
        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(_root, UnixFileMode.None);

        CacheDirectory.EnsureCreated(_root);

        var mode = File.GetUnixFileMode(_root);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void EnsureCreated_OnWindowsProtectsTheAccessRulesOfANewDirectory()
    {
        CacheDirectory.EnsureCreated(_root);

        Assert.IsTrue(AreAccessRulesProtected(_root),
            "a newly created cache directory must block inherited access-control entries");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void EnsureCreated_OnWindowsReprotectsADirectoryThatAlreadyExists()
    {
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
        return new DirectoryInfo(path).GetAccessControl().AreAccessRulesProtected;
    }

    [TestMethod]
    public void BlockFileName_CombinesDriveLetterAndSerial()
    {
        Assert.AreEqual("C-0BADF00D.mlix", CacheDirectory.BlockFileName(new VolumeKey('C'), 0x0BADF00D));
        Assert.AreEqual("C-0BADF00D.mlix", CacheDirectory.BlockFileName(new VolumeKey('c'), 0x0BADF00D));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t")]
    public async Task OpenAsync_BlankCachedDirectory_RejectsConfiguration(string? directory)
    {
        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
        {
            await using var index = await FileIndex.OpenAsync(
                new FileIndexOptions
                {
                    CacheDirectory = directory,
                    Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), _root, 1)
                },
                CancellationToken.None);
        });

        Assert.AreEqual("options", exception.ParamName);
        Assert.Contains("Set FileIndexOptions.CacheDirectory or set FileIndexOptions.CacheMode to Disabled.", exception.Message);
        Assert.IsFalse(Directory.Exists(_root));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OpenAsync_DisabledCacheMode_DoesNotCreateAnUnusedCacheDirectory(bool supplyDirectory)
    {
        await using var index = await FileIndex.OpenAsync(
            new FileIndexOptions
            {
                Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), Path.Combine(_root, "absent"), 1),
                CacheMode = CacheMode.Disabled,
                CacheDirectory = supplyDirectory ? _root : null
            },
            CancellationToken.None);

        Assert.AreEqual(string.Empty, index.CacheDirectoryPath);
        Assert.IsFalse(Directory.Exists(_root));
    }

    [TestMethod]
    public async Task OpenAsync_ExplicitTemporaryCacheDirectory_StillCreatesDirectory()
    {
        Assert.IsFalse(Directory.Exists(_root));
        await using var index = await FileIndex.OpenAsync(
            new FileIndexOptions
            {
                Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), Path.Combine(_root, "absent"), 1),
                CacheDirectory = _root
            },
            CancellationToken.None);

        Assert.AreEqual(_root, index.CacheDirectoryPath);
        Assert.IsTrue(Directory.Exists(_root));
        Assert.IsNull(index.CurrentBlock);
    }
}
