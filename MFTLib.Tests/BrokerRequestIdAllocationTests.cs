using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Request ids are allocated by BrokerProcess.NextRequestId, the same function the process calls for
// every control request; a long-lived session reaches the uint boundary only after four billion
// requests, so the boundary is exercised by passing the last issued id directly.
[TestClass]
public class BrokerRequestIdAllocationTests
{
    [TestMethod]
    public void FirstRequest_GetsOne()
    {
        Assert.AreEqual(1u, BrokerProcess.NextRequestId(0, new Dictionary<uint, object>()));
    }

    [TestMethod]
    public void WrapAround_SkipsZeroAndOutstandingIds()
    {
        var outstanding = new Dictionary<uint, object> { [1] = new() };

        var atMaximum = BrokerProcess.NextRequestId(uint.MaxValue - 1, outstanding);
        var afterWrap = BrokerProcess.NextRequestId(atMaximum, outstanding);

        Assert.AreEqual(uint.MaxValue, atMaximum);
        Assert.AreEqual(2u, afterWrap, "Zero and the outstanding id 1 are skipped.");
    }

    [TestMethod]
    public void OccupiedMaximum_IsSkippedBeforeWrapping()
    {
        var outstanding = new Dictionary<uint, object> { [uint.MaxValue] = new() };

        Assert.AreEqual(1u, BrokerProcess.NextRequestId(uint.MaxValue - 1, outstanding));
    }

    [TestMethod]
    public void NextAfterRelease_ReusesFreedIdOnlyAfterWrap()
    {
        var outstanding = new Dictionary<uint, object> { [1] = new(), [3] = new() };

        Assert.AreEqual(4u, BrokerProcess.NextRequestId(3, outstanding),
            "The walk continues past the last issued id rather than refilling the freed id 2.");
        Assert.AreEqual(2u, BrokerProcess.NextRequestId(uint.MaxValue, outstanding),
            "After the wrap the freed id 2 is the first free id.");
    }
}
