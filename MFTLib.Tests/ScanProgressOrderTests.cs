using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>A root scan's samples reach handlers in phase order with a count that never decreases within a phase.</summary>
[TestClass]
public sealed class ScanProgressOrderTests
{
    static readonly VolumeKey Volume = new('C');

    [TestMethod]
    public void Admit_APhaseEarlierThanOneReported_IsDropped()
    {
        var order = new ScanProgressOrder();

        Assert.IsTrue(order.Admit(Sample(IndexScanPhase.Scanning, 10)));
        Assert.IsTrue(order.Admit(Sample(IndexScanPhase.Indexing, 0)));
        Assert.IsFalse(order.Admit(Sample(IndexScanPhase.Scanning, 20)), "a scan never returns to Scanning");
        Assert.IsTrue(order.Admit(Sample(IndexScanPhase.Indexing, 0)), "a repeated phase at the same count passes");
    }

    [TestMethod]
    public void Admit_ALowerCountWithinAPhase_IsDroppedUntilTheCountPassesTheHighWaterMark()
    {
        var order = new ScanProgressOrder();

        Assert.IsTrue(order.Admit(Sample(IndexScanPhase.Scanning, 100)));
        Assert.IsFalse(order.Admit(Sample(IndexScanPhase.Scanning, 0)), "a growth retry restarts from zero");
        Assert.IsFalse(order.Admit(Sample(IndexScanPhase.Scanning, 99)));
        Assert.IsTrue(order.Admit(Sample(IndexScanPhase.Scanning, 100)));
        Assert.IsTrue(order.Admit(Sample(IndexScanPhase.Scanning, 150)));
    }

    [TestMethod]
    public void Admit_Finished_AlwaysPassesAndStartsTheNextScanAfresh()
    {
        var order = new ScanProgressOrder();
        Assert.IsTrue(order.Admit(Sample(IndexScanPhase.Indexing, 0)));

        Assert.IsTrue(order.Admit(Sample(IndexScanPhase.Finished, 0)));
        Assert.IsTrue(order.Admit(Sample(IndexScanPhase.Finished, 0)), "Finished passes after Finished");
        Assert.IsTrue(order.Admit(Sample(IndexScanPhase.Enumerating, 0)), "a rescan starts from its first phase");
        Assert.IsTrue(order.Admit(Sample(IndexScanPhase.Scanning, 0)), "a later phase with a lower count passes");
    }

    static IndexScanProgress Sample(IndexScanPhase phase, uint count) => new(Volume, phase, count);
}
