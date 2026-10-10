using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class WatchFaultTests
{
    [TestMethod]
    [DataRow(WatchFaultKind.Drive, true)]
    [DataRow(WatchFaultKind.Apply, true)]
    [DataRow(WatchFaultKind.CatchUpLost, false)]
    [DataRow(WatchFaultKind.Subscriber, false)]
    [DataRow(WatchFaultKind.Channel, false)]
    [DataRow(WatchFaultKind.Recovery, false)]
    [DataRow(WatchFaultKind.RescanRestart, false)]
    public void IsRecovering_ClassifiesFaultBoundary(WatchFaultKind kind, bool expected)
    {
        var fault = new WatchFault(kind, 'T', new IOException("watch failure"));

        Assert.AreEqual(expected, fault.IsRecovering);
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public void IsRecovering_CatchUpLossUsesTheReportedRecoveryDecision(bool recoveryStopped, bool expected)
    {
        var fault = new WatchFault(WatchFaultKind.CatchUpLost, 'T',
            new JournalCatchUpLostException(recoveryStopped, "lost scan catch-up"));

        Assert.AreEqual(expected, fault.IsRecovering);
    }

    [TestMethod]
    [DataRow(WatchFaultKind.Subscriber)]
    [DataRow(WatchFaultKind.Channel)]
    [DataRow(WatchFaultKind.Recovery)]
    [DataRow(WatchFaultKind.RescanRestart)]
    public void IsRecovering_ContinuingCatchUpExceptionDoesNotChangeAnotherBoundary(WatchFaultKind kind)
    {
        var fault = new WatchFault(kind, 'T', new JournalCatchUpLostException(false, "lost scan catch-up"));

        Assert.IsFalse(fault.IsRecovering);
    }
}
