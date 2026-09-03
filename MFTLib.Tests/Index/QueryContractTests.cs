using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class QueryContractTests
{
    [TestMethod]
    public void SearchQuery_DefaultsMatchThePublishedContract()
    {
        var query = new SearchQuery("report");

        Assert.AreEqual("report", query.NamePattern);
        Assert.IsFalse(query.CaseSensitive);
        Assert.IsNull(query.Under);
        Assert.IsNull(query.Directories);
        Assert.IsNull(query.MinimumSize);
        Assert.IsNull(query.MaximumSize);
        Assert.IsNull(query.ModifiedAfter);
        Assert.IsNull(query.ModifiedBefore);
    }

    [TestMethod]
    public void SearchQuery_SupportsNamedConstruction()
    {
        var query = new SearchQuery(NamePattern: "*.log", CaseSensitive: true, Directories: false,
            MinimumSize: 100, MaximumSize: 1000);

        Assert.AreEqual("*.log", query.NamePattern);
        Assert.IsTrue(query.CaseSensitive);
        Assert.IsFalse(query.Directories);
        Assert.AreEqual(100L, query.MinimumSize);
        Assert.AreEqual(1000L, query.MaximumSize);
    }

    [TestMethod]
    public void DriveStatus_CarriesEverythingADriveCardNeeds()
    {
        var status = new DriveStatus
        {
            DriveLetter = 'T',
            ProducerKind = ProducerKind.Enumeration,
            State = DriveState.Ready,
            RowCount = 42,
            ScanTimestamp = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
            CompactionNeeded = false,
            WatchSupported = false
        };

        Assert.AreEqual('T', status.DriveLetter);
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(42u, status.RowCount);
        Assert.AreEqual(0, status.AccessDeniedSubtreeCount);
        Assert.IsFalse(status.WatchSupported);
        Assert.IsNull(status.DiscardedBlock);
    }

    [TestMethod]
    public void FileChange_CarriesThePreviousNameOnlyForRenames()
    {
        var created = new FileChange(FileChangeKind.Created, default);
        var renamed = new FileChange(FileChangeKind.Renamed, default, "before.txt");

        Assert.IsNull(created.PreviousName);
        Assert.AreEqual("before.txt", renamed.PreviousName);
        Assert.AreEqual(FileChangeKind.Renamed, renamed.Kind);
    }

    [TestMethod]
    public void DuplicateGroup_HoldsANameAndItsEntries()
    {
        var group = new DuplicateGroup("readme.md", []);
        Assert.AreEqual("readme.md", group.Name);
        Assert.AreEqual(0, group.Entries.Count);
    }
}
