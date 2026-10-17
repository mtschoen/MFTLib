using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class UsnJournalTests
{
    [TestCleanup]
    public void Cleanup()
    {
        FileUtilities.ResetToDefaults();
        Kernel32.ResetToDefaults();
    }

    [TestMethod]
    public void VolumeHandleFactories_OnlyWatchRequestsOverlappedIo()
    {
        var flags = new List<uint>();
        Kernel32._createFile = (path, access, share, security, disposition, attributes, template) =>
        {
            Assert.AreEqual(@"\\.\C:", path);
            Assert.AreEqual(0x80000000u, access);
            Assert.AreEqual(3u, share);
            Assert.AreEqual(3u, disposition);
            Assert.AreEqual(IntPtr.Zero, security);
            Assert.AreEqual(IntPtr.Zero, template);
            flags.Add(attributes);
            return FakeVolumeHandle.Create();
        };

        using var synchronous = FileUtilities._getVolumeHandle(@"\\.\C:");
        using var watch = FileUtilities._getWatchVolumeHandle(@"\\.\C:");

        CollectionAssert.AreEqual(new uint[] { 0, 0x40000000 }, flags.ToArray());
    }

    [TestMethod]
    public void UsnJournalEntry_ReasonHelpers_Work()
    {
        var create = JournalEntries.Create(1, 100, "test.txt", UsnReason.FileCreate);
        Assert.IsTrue(create.IsCreate);
        Assert.IsFalse(create.IsDelete);
        Assert.IsFalse(create.IsClose);
        Assert.IsFalse(create.IsRename);

        var delete = JournalEntries.Create(1, 200, "gone.txt", UsnReason.FileDelete | UsnReason.Close);
        Assert.IsTrue(delete.IsDelete);
        Assert.IsTrue(delete.IsClose);

        var rename = JournalEntries.Create(1, 300, "renamed.txt", UsnReason.RenameNewName);
        Assert.IsTrue(rename.IsRename);
    }

    [TestMethod]
    public void UsnJournalEntry_ToString_IncludesReasonAndName()
    {
        var entry = JournalEntries.Create(42, 100, "test.txt", UsnReason.FileCreate);
        var text = entry.ToString();
        Assert.IsTrue(text.Contains("FileCreate"));
        Assert.IsTrue(text.Contains("test.txt"));
        Assert.IsTrue(text.Contains("42"));
    }

    [TestMethod]
    public void QueryUsnJournal_Disposed_Throws()
    {
        FileUtilities._getVolumeHandle = _ => FakeVolumeHandle.Create();
        var volume = MftVolume.Open("C");
        var journal = volume.Journal;
        volume.Dispose();
        Assert.ThrowsException<ObjectDisposedException>(() => journal.QueryUsnJournalCursor());
        Assert.ThrowsException<ObjectDisposedException>(() => journal.QueryUsnJournalSettings());
        Assert.ThrowsException<ObjectDisposedException>(() => journal.GrowUsnJournal(4096, 1024));
    }

    [TestMethod]
    public void ReadUsnJournal_Disposed_Throws()
    {
        FileUtilities._getVolumeHandle = _ => FakeVolumeHandle.Create();
        var volume = MftVolume.Open("C");
        var journal = volume.Journal;
        volume.Dispose();
        Assert.ThrowsException<ObjectDisposedException>(() => journal.ReadUsnJournalBounded(new UsnJournalCursor(1, 0), 0));
    }

    [TestMethod]
    public async Task WatchUsnJournal_Disposed_Throws()
    {
        FileUtilities._getVolumeHandle = _ => FakeVolumeHandle.Create();
        var volume = MftVolume.Open("C");
        var journal = volume.Journal;
        volume.Dispose();
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in journal.WatchUsnJournal(new UsnJournalCursor(1, 0)))
            {
            }
        });
    }

    [TestMethod]
    public void UsnJournalEntry_PositiveTimestamp_ParsesAsDateTime()
    {
        // 2020-01-01 00:00:00 UTC as FILETIME
        var filetime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var entry = new UsnJournalEntry(new NativeUsnJournalEntryData
        {
            RecordNumber = 1,
            ParentRecordNumber = 5,
            SequenceNumber = 0,
            Usn = 100,
            FileTimeTimestamp = filetime,
            Reason = (uint)UsnReason.FileCreate,
            FileAttributes = 0x20,
            FileName = "timestamped.txt"
        });

        Assert.AreEqual(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), entry.TimestampUtc);
        Assert.AreEqual(100L, entry.Usn);
        Assert.AreEqual(5UL, entry.ParentRecordNumber);
        Assert.AreEqual(FileAttributes.Archive, entry.FileAttributes);
    }

    [TestMethod]
    public void UsnJournalEntry_ZeroTimestamp_ReturnsMinValue()
    {
        var entry = new UsnJournalEntry(new NativeUsnJournalEntryData
        {
            RecordNumber = 1,
            ParentRecordNumber = 5,
            SequenceNumber = 0,
            Usn = 100,
            FileTimeTimestamp = 0,
            Reason = (uint)UsnReason.FileCreate,
            FileAttributes = 0,
            FileName = "test.txt"
        });
        Assert.AreEqual(DateTime.MinValue, entry.TimestampUtc);
    }

    [TestMethod]
    public void UsnJournalEntry_Create_RoundTripsDecodedValues()
    {
        var timestamp = new DateTime(2021, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var entry = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 77,
            ParentRecordNumber = 5,
            Usn = 9000,
            TimestampUtc = timestamp,
            Reason = UsnReason.FileCreate | UsnReason.Close,
            FileAttributes = FileAttributes.Archive,
            FileName = "reconstructed.txt"
        });

        Assert.AreEqual(77UL, entry.RecordNumber);
        Assert.AreEqual(5UL, entry.ParentRecordNumber);
        Assert.AreEqual(9000L, entry.Usn);
        Assert.AreEqual(timestamp, entry.TimestampUtc);
        Assert.AreEqual(UsnReason.FileCreate | UsnReason.Close, entry.Reason);
        Assert.AreEqual(FileAttributes.Archive, entry.FileAttributes);
        Assert.AreEqual("reconstructed.txt", entry.FileName);
        Assert.IsTrue(entry.IsCreate);
        Assert.IsTrue(entry.IsClose);
    }

    [TestMethod]
    public void UsnJournalCursor_StoresValues()
    {
        var cursor = new UsnJournalCursor(0xDEADBEEF, 12345);
        Assert.AreEqual(0xDEADBEEFUL, cursor.JournalIdentifier);
        Assert.AreEqual(12345L, cursor.NextUsn);
    }
}
