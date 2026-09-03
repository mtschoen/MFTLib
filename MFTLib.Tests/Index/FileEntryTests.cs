using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                     "throughout rather than IDisposable on the test class itself.")]
public class FileEntryTests
{
    static readonly DateTime ScanMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime FileMoment = new(2026, 8, 30, 14, 5, 0, DateTimeKind.Utc);

    SyntheticBlockBuilder _builder = null!;
    Snapshot _snapshot = null!;
    uint _directoryRow;
    uint _fileRow;
    uint _deletedRow;

    [TestInitialize]
    public void Initialize()
    {
        _builder = new SyntheticBlockBuilder();
        var root = _builder.AddRoot();
        _directoryRow = _builder.AddRow("Documents", root, RowFlags.InUse | RowFlags.Directory, 0, ScanMoment,
            attributes: (uint)FileAttributes.Directory);
        _fileRow = _builder.AddRow("report.pdf", _directoryRow, RowFlags.InUse, 4096, FileMoment,
            attributes: (uint)FileAttributes.Archive);
        _deletedRow = _builder.AddRow("gone.tmp", _directoryRow, RowFlags.InUse | RowFlags.Tombstone, 10,
            FileMoment);
        _builder.Complete(ScanMoment);

        var block = _builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock(_builder.DriveLetter, 0, block, deleteFileOnRelease: false);
        _snapshot = Snapshot.Create([driveBlock]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _snapshot.ReleaseNow();
        _builder.Dispose();
    }

    [TestMethod]
    public void FileEntry_IsSixteenBytes()
    {
        Assert.AreEqual(16, Unsafe.SizeOf<FileEntry>());
    }

    [TestMethod]
    public void Properties_ReadStraightFromTheMappedRow()
    {
        var entry = FileEntry.Create(_snapshot, 0, _fileRow);

        Assert.AreEqual("report.pdf", entry.Name);
        Assert.AreEqual(4096L, entry.Size);
        Assert.IsTrue(entry.SizeKnown);
        Assert.AreEqual(FileMoment, entry.Modified);
        Assert.AreEqual(FileAttributes.Archive, entry.Attributes);
        Assert.IsFalse(entry.IsDirectory);
        Assert.IsFalse(entry.IsDeleted);
        Assert.IsTrue(entry.IsValid);
    }

    [TestMethod]
    public void DirectoryEntry_ReadsAsDirectoryWithZeroSize()
    {
        var entry = FileEntry.Create(_snapshot, 0, _directoryRow);
        Assert.IsTrue(entry.IsDirectory);
        Assert.AreEqual(0L, entry.Size);
        Assert.AreEqual("Documents", entry.Name);
    }

    [TestMethod]
    public void TombstonedEntry_ReadsAsDeletedAndKeepsItsName()
    {
        var entry = FileEntry.Create(_snapshot, 0, _deletedRow);
        Assert.IsTrue(entry.IsDeleted);
        Assert.AreEqual("gone.tmp", entry.Name);
    }

    [TestMethod]
    public void Id_CarriesDriveLetterRowNumberAndProducerKind()
    {
        var entry = FileEntry.Create(_snapshot, 0, _fileRow);
        var id = entry.Id;

        Assert.AreEqual('T', id.DriveLetter);
        Assert.AreEqual(_fileRow, id.RecordNumber);
        Assert.AreEqual(ProducerKind.Enumeration, id.ProducerKind);
        Assert.IsTrue(id.IsSynthetic);
    }

    [TestMethod]
    public void SizeUnknownRow_ReportsSizeNotKnown()
    {
        using var builder = new SyntheticBlockBuilder('V');
        var root = builder.AddRoot();
        var row = builder.AddRow("huge.bin", root, RowFlags.InUse | RowFlags.SizeUnknown, 0, FileMoment);
        builder.Complete(ScanMoment);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('V', 0, block, deleteFileOnRelease: false)]);
        try
        {
            var entry = FileEntry.Create(snapshot, 0, row);
            Assert.IsFalse(entry.SizeKnown);
            Assert.AreEqual(0L, entry.Size);
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void DefaultEntry_IsNotValidAndDoesNotThrowOnIsValid()
    {
        var entry = default(FileEntry);
        Assert.IsFalse(entry.IsValid);
    }

    [TestMethod]
    public void Equality_ComparesSnapshotDriveAndRow()
    {
        var first = FileEntry.Create(_snapshot, 0, _fileRow);
        var second = FileEntry.Create(_snapshot, 0, _fileRow);
        var other = FileEntry.Create(_snapshot, 0, _directoryRow);

        Assert.AreEqual(first, second);
        Assert.AreNotEqual(first, other);
    }

    [TestMethod]
    public void ToString_NamesTheEntryAndItsRow()
    {
        var entry = FileEntry.Create(_snapshot, 0, _fileRow);
        StringAssert.Contains(entry.ToString(), "report.pdf");
    }

    [TestMethod]
    public void Open_ProducerKindIsNotEnumeration_ThrowsNotSupported()
    {
        using var builder = new SyntheticBlockBuilder('M');
        var root = builder.AddRoot();
        var fileRow = builder.AddRow("record.dat", root, RowFlags.InUse, 128, FileMoment);
        builder.Complete(ScanMoment);
        builder.MutateHeader((ref header) => header.ProducerKind = ProducerKind.Mft);

        var block = builder.OpenForReading(out _)!;
        var driveBlock = new DriveBlock(builder.DriveLetter, 0, block, deleteFileOnRelease: false);
        var snapshot = Snapshot.Create([driveBlock]);
        try
        {
            var entry = FileEntry.Create(snapshot, 0, fileRow);
            Assert.ThrowsException<NotSupportedException>(() => entry.Open(FileAccess.Read));
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Open_NoRootDirectoryConfigured_ThrowsInvalidOperation()
    {
        // Initialize's DriveBlock is built without a rootDirectoryPath, matching the case
        // documented on DriveBlock.RootDirectoryPath: a synthetic block that never calls Open.
        var entry = FileEntry.Create(_snapshot, 0, _fileRow);
        Assert.ThrowsException<InvalidOperationException>(() => entry.Open(FileAccess.Read));
    }
}
