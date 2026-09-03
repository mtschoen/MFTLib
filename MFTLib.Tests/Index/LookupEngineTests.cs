using System.Diagnostics.CodeAnalysis;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                     "throughout rather than IDisposable on the test class itself.")]
public class LookupEngineTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    SyntheticBlockBuilder _firstBuilder = null!;
    SyntheticBlockBuilder _secondBuilder = null!;
    Snapshot _snapshot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _firstBuilder = new SyntheticBlockBuilder();
        var firstRoot = _firstBuilder.AddRoot();
        var documents = _firstBuilder.AddRow("Documents", firstRoot, RowFlags.InUse | RowFlags.Directory, 0, Moment);
        _firstBuilder.AddRow("report.pdf", documents, RowFlags.InUse, 4096, Moment);
        _firstBuilder.AddRow("readme.md", documents, RowFlags.InUse, 12, Moment);
        _firstBuilder.Complete(Moment);

        _secondBuilder = new SyntheticBlockBuilder('U');
        var secondRoot = _secondBuilder.AddRoot();
        _secondBuilder.AddRow("readme.md", secondRoot, RowFlags.InUse, 15, Moment);
        _secondBuilder.Complete(Moment);

        var firstBlock = _firstBuilder.OpenForReading(out _)!;
        var secondBlock = _secondBuilder.OpenForReading(out _)!;
        _snapshot = Snapshot.Create([
            new DriveBlock('T', 0, firstBlock, deleteFileOnRelease: false),
            new DriveBlock('U', 1, secondBlock, deleteFileOnRelease: false)
        ]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _snapshot.ReleaseNow();
        _firstBuilder.Dispose();
        _secondBuilder.Dispose();
    }

    [TestMethod]
    public void Find_ResolvesAFullPathToItsEntry()
    {
        var entry = LookupEngineTestAccess.Find(_snapshot, @"T:\Documents\report.pdf");
        Assert.IsTrue(entry.HasValue);
        Assert.AreEqual("report.pdf", entry.Value.Name);
        Assert.AreEqual(4096L, entry.Value.Size);
    }

    [TestMethod]
    public void Find_IsCaseInsensitiveOnPathSegments()
    {
        var entry = LookupEngineTestAccess.Find(_snapshot, @"t:\DOCUMENTS\Report.PDF");
        Assert.IsTrue(entry.HasValue);
        Assert.AreEqual("report.pdf", entry.Value.Name);
    }

    [TestMethod]
    public void Find_DriveRootReturnsTheRootEntry()
    {
        var entry = LookupEngineTestAccess.Find(_snapshot, @"T:\");
        Assert.IsTrue(entry.HasValue);
        Assert.AreEqual(@"T:\", entry.Value.Path);
    }

    [TestMethod]
    public void Find_MissingSegmentReturnsNull()
    {
        Assert.IsNull(LookupEngineTestAccess.Find(_snapshot, @"T:\Documents\nothing.txt"));
        Assert.IsNull(LookupEngineTestAccess.Find(_snapshot, @"T:\Nowhere\report.pdf"));
    }

    [TestMethod]
    public void Find_UnknownDriveReturnsNull()
    {
        Assert.IsNull(LookupEngineTestAccess.Find(_snapshot, @"Z:\Documents\report.pdf"));
    }

    [TestMethod]
    public void Find_MalformedPathReturnsNull()
    {
        Assert.IsNull(LookupEngineTestAccess.Find(_snapshot, "not-a-path"));
        Assert.IsNull(LookupEngineTestAccess.Find(_snapshot, ""));
    }

    [TestMethod]
    public void FindByName_SpansEveryDriveInTheSnapshot()
    {
        var results = LookupEngineTestAccess.FindByName(_snapshot, "readme.md", caseSensitive: false);
        Assert.AreEqual(2, results.Count);
        CollectionAssert.AreEquivalent(new[] { 'T', 'U' }, results.Select(entry => entry.Id.DriveLetter).ToArray());
    }

    [TestMethod]
    public void FindByName_IsAnExactNameMatchNotASubstring()
    {
        Assert.AreEqual(0, LookupEngineTestAccess.FindByName(_snapshot, "readme", caseSensitive: false).Count);
        Assert.AreEqual(2, LookupEngineTestAccess.FindByName(_snapshot, "README.MD", caseSensitive: false).Count);
        Assert.AreEqual(0, LookupEngineTestAccess.FindByName(_snapshot, "README.MD", caseSensitive: true).Count);
    }

    [TestMethod]
    public void Root_ReturnsTheDriveRootForEachDrive()
    {
        Assert.AreEqual(@"T:\", LookupEngineTestAccess.Root(_snapshot, 'T').Path);
        Assert.AreEqual(@"U:\", LookupEngineTestAccess.Root(_snapshot, 'u').Path);
    }

    [TestMethod]
    public void Root_UnknownDriveThrows()
    {
        Assert.ThrowsException<ArgumentException>(() => LookupEngineTestAccess.Root(_snapshot, 'Z'));
    }

    [TestMethod]
    public void RootAndFind_UseTheRootRowDeclaredByTheBlockHeader()
    {
        using var builder = new SyntheticBlockBuilder('V');
        for (var metadataRow = 0u; metadataRow < 5; metadataRow++)
        {
            builder.AddRow($"metadata-{metadataRow}", metadataRow, RowFlags.InUse, 0, Moment);
        }

        var rootRow = builder.AddRow("", 5, RowFlags.InUse | RowFlags.Directory, 0, Moment);
        var documentsRow = builder.AddRow("Documents", rootRow,
            RowFlags.InUse | RowFlags.Directory, 0, Moment);
        builder.AddRow("report.pdf", documentsRow, RowFlags.InUse, 4096, Moment);
        builder.MutateHeader((ref BlockHeader header) => header.RootRow = rootRow);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out var validation)!;
        Assert.AreEqual(BlockValidationResult.Valid, validation);
        var snapshot = Snapshot.Create([new DriveBlock('V', 0, block, deleteFileOnRelease: false)]);
        try
        {
            Assert.AreEqual(rootRow, LookupEngineTestAccess.Root(snapshot, 'V').RowIndexForTest());
            var entry = LookupEngineTestAccess.Find(snapshot, @"V:\Documents\report.pdf");
            Assert.IsTrue(entry.HasValue);
            Assert.AreEqual("report.pdf", entry.Value.Name);
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }
}

static class LookupEngineTestAccess
{
    public static FileEntry? Find(Snapshot snapshot, string fullPath)
    {
        return LookupEngine.Find(snapshot, fullPath);
    }

    public static List<FileEntry> FindByName(Snapshot snapshot, string name, bool caseSensitive)
    {
        return LookupEngine.FindByName(snapshot, name, caseSensitive);
    }

    public static FileEntry Root(Snapshot snapshot, char driveLetter)
    {
        return LookupEngine.Root(snapshot, driveLetter);
    }
}
