using MFTLib.Index;
using MFTLibTestExtensions;
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
    public void DriveStatus_DefaultsMatchThePublishedContract()
    {
        var status = SyntheticNotifications.CreateDriveStatus('T', DriveState.Ready, BlockSource.None, 40, false,
            new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.AreEqual(0, status.Block.AccessDeniedSubtreeCount);
        Assert.IsNull(status.Watch.FailureMessage);
        Assert.AreEqual(DriveFailureKind.None, status.FailureKind);
    }

    [TestMethod]
    public void FileChange_CarriesThePreviousPathOnlyForRenames()
    {
        var moment = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
        var created = new FileChange(FileChangeKind.Created, default, "T:\\", moment);
        var renamed = new FileChange(FileChangeKind.Renamed, default, "T:\\renamed.txt", moment, "T:\\before.txt");

        Assert.IsNull(created.PreviousPath);
        Assert.AreEqual("T:\\before.txt", renamed.PreviousPath);
        Assert.AreEqual(FileChangeKind.Renamed, renamed.Kind);
        Assert.AreEqual(moment, created.Timestamp);
        Assert.AreEqual(moment, renamed.Timestamp);
    }
}
