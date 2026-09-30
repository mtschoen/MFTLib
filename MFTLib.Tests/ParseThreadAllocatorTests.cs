using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class ParseThreadAllocatorTests
{
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Release_SameRegistrationTwice_ReleasesItOnceAndLeavesOthersUntouched()
    {
        var allocator = new ParseThreadAllocator(4);
        using var first = await allocator.AdmitAsync(CancellationToken.None);
        using var second = await allocator.AdmitAsync(CancellationToken.None);
        Assert.AreEqual(2, second.Allowance.Count);

        allocator.Release(first);
        allocator.Release(first);

        Assert.AreEqual(1, allocator.RunningScanCount, "The second release finds nothing to remove.");
        Assert.AreEqual(4, second.Allowance.Count, "The survivor holds every processor, as after one release.");
    }

    [TestMethod]
    public async Task AdmitAsync_WaitCancelledInTheMomentItIsAdmitted_ReturnsTheRegistration()
    {
        var allocator = new ParseThreadAllocator(1);
        using var running = await allocator.AdmitAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = allocator.AdmitAsync(cancellation.Token).AsTask();
        Assert.AreEqual(1, allocator.QueuedScanCount);

        // Cancellation callbacks run last-registered first: this one runs before the allocator's
        // own, and admits the waiter, so the allocator's callback finds the waiter already off the
        // queue and leaves its registration standing.
        cancellation.Token.Register(static state =>
        {
            var (owner, registration) = ((ParseThreadAllocator, ParseThreadRegistration))state!;
            owner.Release(registration);
        }, (allocator, running));
        await cancellation.CancelAsync();

        using var admitted = await waiting.WaitAsync(HangGuard);
        Assert.AreEqual(1, allocator.RunningScanCount);
        Assert.AreEqual(0, allocator.QueuedScanCount);
        Assert.AreEqual(1, admitted.Allowance.Count);
    }
}
