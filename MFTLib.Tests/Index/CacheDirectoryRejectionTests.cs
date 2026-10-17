using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class CacheDirectoryRejectionTests
{
    string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"mftlib-rejection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    string WriteFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "foreign content");
        return path;
    }

    string CreateBlock()
    {
        var path = Path.Combine(_directory, CacheDirectory.BlockFileName('T', 1));
        using var block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = path,
            VolumeSerial = 1,
            ProducerKind = ProducerKind.Enumeration,
            RootRow = 0,
            SlotCapacity = 8,
            NamePoolCapacity = 4096
        });
        var writer = new BlockWriter(block);
        Assert.IsTrue(writer.TryWriteRow(0, _directory,
            new RowColumns(0, RowFlags.InUse, 0, 0, 0, 0)));
        writer.Complete(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);
        block.Flush(null);
        return path;
    }

    string[] ReadPaths(string operation, IReadOnlySet<char>? drives,
        Action<CachedBlockRejection>? rejectedFile)
    {
        switch (operation)
        {
            case "enumerate":
                return CacheDirectory.EnumerateCached(_directory, rejectedFile)
                    .Select(file => file.Path).ToArray();
            case "inspect":
            case "inspect-default-drives":
                var statuses = operation == "inspect-default-drives"
                    ? CacheDirectory.InspectCached(_directory, rejectedFile: rejectedFile)
                    : CacheDirectory.InspectCached(_directory,
                        drives?.Select(letter => new DriveKey(letter)), rejectedFile);
                Assert.IsTrue(statuses.All(status =>
                    status.Availability == CachedBlockAvailability.Available));
                return statuses.Select(status => status.File.Path).ToArray();
            case "delete":
                var results = CacheDirectory.DeleteCached(_directory,
                    drives?.Select(letter => new DriveKey(letter)), rejectedFile);
                Assert.IsTrue(results.All(result => result.Outcome == CachedBlockDeletionOutcome.Deleted));
                return results.Select(result => result.File.Path).ToArray();
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
        var canonical = CreateBlock();
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

        CollectionAssert.AreEqual(new[] { canonical }, paths);
        CollectionAssert.AreEquivalent(foreign, rejected.Select(entry => entry.Path).ToArray());
        Assert.IsTrue(rejected.All(entry => entry.Reason == "Invalid block filename."));
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
        var canonical = CreateBlock();
        var foreign = WriteFile("t-invalid.mlix");
        IReadOnlySet<char> drives = empty ? new HashSet<char>() : new HashSet<char> { 'U' };
        var rejected = new List<CachedBlockRejection>();

        Assert.AreEqual(0, ReadPaths(operation, drives, rejected.Add).Length);

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
        var canonical = CreateBlock();
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
        Assert.AreEqual(0, ReadPaths(operation, null, rejected.Add).Length);
        Directory.Delete(_directory);
        Assert.AreEqual(0, ReadPaths(operation, null, rejected.Add).Length);
        Assert.AreEqual(0, rejected.Count);
        Directory.CreateDirectory(_directory);
        var foreign = WriteFile("t-invalid.mlix");
        Assert.AreEqual(0, ReadPaths(operation, null, null).Length);
        Assert.AreEqual("foreign content", File.ReadAllText(foreign));
    }
}
