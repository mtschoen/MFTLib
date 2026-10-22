using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndexOptions.DirectoryWalk" /> is validated at <see cref="FileIndex.OpenAsync" />
///     admission, before any cache directory exists or any scan starts.
/// </summary>
[TestClass]
public sealed class FileIndexDirectoryWalkAdmissionTests : IDisposable
{
    IndexTreeFixture _fixture = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize() => _fixture = new IndexTreeFixture();

    public void Dispose() => _fixture.Dispose();

    async Task AssertRefusedBeforeAnySideEffect(DirectoryWalkOptions? walk, string expectedText)
    {
        var options = _fixture.Options() with { DirectoryWalk = walk! };

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            FileIndex.OpenAsync(options, TestContext.CancellationToken));

        Assert.Contains(expectedText, exception.Message);
        Assert.IsFalse(Directory.Exists(_fixture.CacheDirectory));
    }

    [TestMethod]
    public Task OpenAsync_NullWalkOptions_Throws() =>
        AssertRefusedBeforeAnySideEffect(null, "DirectoryWalk");

    [TestMethod]
    public Task OpenAsync_NullNameCollection_Throws() =>
        AssertRefusedBeforeAnySideEffect(new DirectoryWalkOptions { PrunedDirectoryNames = null! }, "PrunedDirectoryNames");

    [TestMethod]
    public Task OpenAsync_NullPathCollection_Throws() =>
        AssertRefusedBeforeAnySideEffect(new DirectoryWalkOptions { PrunedDirectoryPaths = null! }, "PrunedDirectoryPaths");

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public Task OpenAsync_BlankName_Throws(string? name) =>
        AssertRefusedBeforeAnySideEffect(new DirectoryWalkOptions { PrunedDirectoryNames = [name!] }, "PrunedDirectoryNames");

    [TestMethod]
    [DataRow(0xD800, true)]
    [DataRow(0xD801, true)]
    [DataRow(0xDC00, false)]
    public Task OpenAsync_NameWithAnUnpairedSurrogate_Throws(int codeUnit, bool trailing) =>
        AssertRefusedBeforeAnySideEffect(
            new DirectoryWalkOptions { PrunedDirectoryNames = [MalformedText.Make(codeUnit, trailing)] }, "unpaired surrogate");

    [TestMethod]
    public async Task OpenAsync_NameWithTheReplacementCharacterOrAValidPair_IsAdmitted()
    {
        var options = _fixture.Options() with
        {
            DirectoryWalk = new DirectoryWalkOptions { PrunedDirectoryNames = ["x\uFFFD", "x\uD83D\uDE00"] }
        };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
    }

    [TestMethod]
    public Task OpenAsync_NameWithASeparator_Throws() =>
        AssertRefusedBeforeAnySideEffect(
            new DirectoryWalkOptions { PrunedDirectoryNames = ["node_modules" + Path.DirectorySeparatorChar + "cache"] },
            "PrunedDirectoryNames");

    [TestMethod]
    public Task OpenAsync_RelativePath_Throws() =>
        AssertRefusedBeforeAnySideEffect(new DirectoryWalkOptions { PrunedDirectoryPaths = ["Documents"] }, "PrunedDirectoryPaths");

    [TestMethod]
    public Task OpenAsync_BlankPath_Throws() =>
        AssertRefusedBeforeAnySideEffect(new DirectoryWalkOptions { PrunedDirectoryPaths = [" "] }, "PrunedDirectoryPaths");

    [TestMethod]
    [DataRow(0xD800)]
    [DataRow(0xD801)]
    public Task OpenAsync_PathWithAnUnpairedSurrogate_Throws(int codeUnit) =>
        AssertRefusedBeforeAnySideEffect(
            new DirectoryWalkOptions { PrunedDirectoryPaths = [Path.Combine(_fixture.TreeRoot, MalformedText.Make(codeUnit, true))] },
            "unpaired surrogate");

    [TestMethod]
    [DataRow(FileAttributes.Directory)]
    [DataRow(FileAttributes.ReparsePoint)]
    [DataRow(FileAttributes.Hidden | FileAttributes.Directory)]
    public Task OpenAsync_AttributeMaskNamingDirectoryOrReparsePoint_Throws(FileAttributes attributes) =>
        AssertRefusedBeforeAnySideEffect(new DirectoryWalkOptions { PrunedDirectoryAttributes = attributes },
            "PrunedDirectoryAttributes");

    [TestMethod]
    public async Task OpenAsync_MftDrivesStillAdmitTheWalkOptions()
    {
        var options = _fixture.Options(producer: ProducerKind.Mft) with
        {
            DirectoryWalk = new DirectoryWalkOptions { PrunedDirectoryNames = ["bad/name", @"bad\name"] }
        };

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            FileIndex.OpenAsync(options, TestContext.CancellationToken));
        Assert.IsFalse(Directory.Exists(_fixture.CacheDirectory));
    }

    [TestMethod]
    public async Task OpenAsync_CopiesTheCollectionsOnceSoLaterEditsDoNotChangeTheWalk()
    {
        var names = new List<string> { "Documents" };
        var options = _fixture.Options() with { DirectoryWalk = new DirectoryWalkOptions { PrunedDirectoryNames = names } };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        names.Clear();

        Assert.IsNotNull(index.Find(Path.Combine(_fixture.TreeRoot, "Documents")));
        Assert.IsNull(index.Find(Path.Combine(_fixture.TreeRoot, "Documents", "readme.md")));
    }

    [TestMethod]
    public void DefaultOptions_PruneNothing()
    {
        var walk = new FileIndexOptions().DirectoryWalk;

        Assert.IsEmpty(walk.PrunedDirectoryNames);
        Assert.IsEmpty(walk.PrunedDirectoryPaths);
        Assert.AreEqual(default, walk.PrunedDirectoryAttributes);
        Assert.IsFalse(walk.PruneDotDirectories);
    }
}
