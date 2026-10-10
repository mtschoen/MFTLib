using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class DriveStatusWatchStateTests
{
    [TestMethod]
    [DataRow(WatchCatchUpState.NotStarted, 0L)]
    [DataRow(WatchCatchUpState.CatchingUp, 1L)]
    [DataRow(WatchCatchUpState.CaughtUp, 2L)]
    [DataRow(WatchCatchUpState.Recovering, 37L)]
    [DataRow(WatchCatchUpState.Faulted, long.MaxValue)]
    public void ToWatchState_PreservesSnapshotStateAndVersionWithoutInventingAFault(
        WatchCatchUpState state, long version)
    {
        var status = new DriveStatus('T', DriveState.Ready)
        {
            Watch = new DriveWatchStatus
            {
                CatchUpState = state,
                StateVersion = version,
                FailureMessage = "existing status detail"
            }
        };

        Assert.AreEqual(new DriveWatchState('T', state, version, null), status.ToWatchState());
    }

    [TestMethod]
    public void ToWatchState_DefaultWatchUsesTheCapturedDrive()
    {
        var status = new DriveStatus('U', DriveState.Offline);

        Assert.AreEqual(new DriveWatchState('U', WatchCatchUpState.NotStarted, 0, null), status.ToWatchState());
    }
}
