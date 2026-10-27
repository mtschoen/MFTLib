using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class CacheDirectoryRejectionTests : IDisposable
{
    TempDirectory _directoryOwner = null!;
    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directoryOwner = new TempDirectory("mftlib-rejection");
        _directory = _directoryOwner.Path;
    }

    [TestCleanup]
    public void Cleanup() => _directoryOwner.Dispose();

    public void Dispose() => Cleanup();

    string WriteFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "foreign content");
        return path;
    }

    string[] ReadPaths(string operation, IReadOnlySet<char>? drives,
        Action<CachedBlockRejection>? rejectedFile)
    {
        switch (operation)
        {
            case "enumerate":
                return [.. CacheDirectory.EnumerateCached(_directory, rejectedFile).Select(file => file.Path)];
            case "inspect":
            case "inspect-default-drives":
                var statuses = operation == "inspect-default-drives"
                    ? CacheDirectory.InspectCached(_directory, rejectedFile: rejectedFile)
                    : CacheDirectory.InspectCached(_directory,
                        drives?.Select(letter => (RootKey)new VolumeKey(letter)), rejectedFile);
                Assert.IsTrue(statuses.All(status =>
                    status.Availability == CachedBlockAvailability.Available));
                return [.. statuses.Select(status => status.File.Path)];
            case "delete":
                var results = CacheDirectory.DeleteCached(_directory,
                    drives?.Select(letter => (RootKey)new VolumeKey(letter)), rejectedFile);
                Assert.IsTrue(results.All(result => result.Outcome == CachedBlockDeletionOutcome.Deleted));
                return [.. results.Select(result => result.File.Path)];
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    [TestMethod]
    [DataRow("enumerate")]
    [DataRow("inspect")]
    [DataRow("inspect-default-drives", DisplayName = "F20: named callback with default drive selection")]
    [DataRow("delete")]
    public void Inventory_ReportsRejectedPathsAndPreservesForeignFiles(string operation)
    {
        var canonical = TestBlocks.WriteCache(_directory, 'T', volumeSerial: 1);
        var foreign = new[]
        {
            WriteFile("t-invalid.mlix"),
            WriteFile("u-00000002.mlix"),
            WriteFile("V-0badf00d.mlix"),
            WriteFile("W-ZZZZZZZZ.mlix"),
            WriteFile("X_00000003.mlix")
        };
        var ignored = new[]
        {
            WriteFile("notes.txt"),
            WriteFile("Y-00000004.mlix.lock"),
            WriteFile("Z-00000005.mlix.retired-test")
        };
        var subdirectory = Path.Combine(_directory, "nested.mlix");
        Directory.CreateDirectory(subdirectory);
        var nested = Path.Combine(subdirectory, "bad.mlix");
        File.WriteAllText(nested, "nested content");
        var rejected = new List<CachedBlockRejection>();

        var paths = ReadPaths(operation, null, rejected.Add);

        Assert.AreSequenceEqual([canonical], paths);
        Assert.AreSequenceEqual(foreign, [.. rejected.Select(entry => entry.Path)], SequenceOrder.InAnyOrder);
        Assert.IsTrue(rejected.All(entry => entry.Message == "Invalid block filename."));
        foreach (var path in foreign.Concat(ignored))
        {
            Assert.AreEqual("foreign content", File.ReadAllText(path));
            Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(path)));
        }
        Assert.AreEqual("nested content", File.ReadAllText(nested));
        Assert.AreEqual(operation != "delete", File.Exists(canonical));
    }

    [TestMethod]
    [DataRow("inspect", false)]
    [DataRow("inspect", true)]
    [DataRow("delete", false)]
    [DataRow("delete", true)]
    public void Rejections_AreReportedEvenWhenCanonicalDrivesAreExcluded(string operation, bool empty)
    {
        var canonical = TestBlocks.WriteCache(_directory, 'T', volumeSerial: 1);
        var foreign = WriteFile("t-invalid.mlix");
        IReadOnlySet<char> drives = empty ? new HashSet<char>() : new HashSet<char> { 'U' };
        var rejected = new List<CachedBlockRejection>();

        Assert.IsEmpty(ReadPaths(operation, drives, rejected.Add));

        Assert.AreEqual(foreign, rejected.Single().Path);
        Assert.IsTrue(File.Exists(canonical));
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(canonical)));
        Assert.AreEqual("foreign content", File.ReadAllText(foreign));
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(foreign)));
    }

    [TestMethod]
    [DataRow("enumerate")]
    [DataRow("inspect")]
    [DataRow("inspect-default-drives", DisplayName = "F20: named callback with default drive selection")]
    [DataRow("delete")]
    public void RejectionCallbackFailure_PropagatesBeforeCanonicalOperations(string operation)
    {
        var canonical = TestBlocks.WriteCache(_directory, 'T', volumeSerial: 1);
        var foreign = WriteFile("t-invalid.mlix");
        var expected = new IOException("observer failed");

        var actual = Assert.ThrowsExactly<IOException>(() =>
            ReadPaths(operation, null, _ => throw expected));

        Assert.AreSame(expected, actual);
        Assert.IsTrue(File.Exists(canonical));
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(canonical)));
        Assert.AreEqual("foreign content", File.ReadAllText(foreign));
        Assert.IsFalse(File.Exists(BlockOwnerLock.LockPathFor(foreign)));
    }

    [TestMethod]
    [DataRow("enumerate")]
    [DataRow("inspect")]
    [DataRow("inspect-default-drives", DisplayName = "F20: named callback with default drive selection")]
    [DataRow("delete")]
    public void Reporting_IsOptionalAndEmptyDirectoriesDoNotNotify(string operation)
    {
        var rejected = new List<CachedBlockRejection>();
        Assert.IsEmpty(ReadPaths(operation, null, rejected.Add));
        Directory.Delete(_directory);
        Assert.IsEmpty(ReadPaths(operation, null, rejected.Add));
        Assert.IsEmpty(rejected);
        Directory.CreateDirectory(_directory);
        var foreign = WriteFile("t-invalid.mlix");
        Assert.IsEmpty(ReadPaths(operation, null, null));
        Assert.AreEqual("foreign content", File.ReadAllText(foreign));
    }
}
