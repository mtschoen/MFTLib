using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class FileIndexSetLifetimeTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    static int BorrowCount(FileIndex index) =>
        index.CurrentBlock is { } block ? block.ReleaseState.Hooks.OutstandingBorrowCount : 0;

    [TestMethod]
    public async Task DisposedMember_CausesEverySetEntryPointToThrowObjectDisposedException()
    {
        using var fixture = new TwoDriveQueryFixture();
        await using var first = await fixture.OpenAsync();
        var second = await fixture.OpenSecondAsync();
        var set = new FileIndexSet([first, second]);

        await second.DisposeAsync();

        // Even a path under healthy first index throws when any member in the set is disposed.
        Assert.ThrowsExactly<ObjectDisposedException>(() => set.Find(Path.Combine(fixture.FirstRoot, "Documents", "readme.md"), Token));
        Assert.ThrowsExactly<ObjectDisposedException>(() => set.Search(new SearchQuery(null), Token));
        Assert.ThrowsExactly<ObjectDisposedException>(() => set.Enumerate(new SearchQuery(null), Token));
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
        {
            var enumerable = set.EnumerateRows(new SearchQuery(null), Token);
            using var enumerator = enumerable.GetEnumerator();
        });
        Assert.AreEqual(0, BorrowCount(first));
    }

    [TestMethod]
    public async Task Enumerate_BorrowsOneMemberAtATimeAndReleasesOnTransitionAndEarlyBreak()
    {
        using var fixture = new TwoDriveQueryFixture();
        await using var first = await fixture.OpenAsync();
        await using var second = await fixture.OpenSecondAsync();
        var set = new FileIndexSet([first, second]);

        var seenT = false;
        var seenU = false;
        foreach (var entry in set.Enumerate(new SearchQuery("readme.md", NameMatchMode.Exact), Token))
        {
            if (entry.Drive == new VolumeKey('T'))
            {
                seenT = true;
                Assert.AreEqual(1, BorrowCount(first), "first member is borrowed while yielding its entries");
                Assert.AreEqual(0, BorrowCount(second), "second member is not yet borrowed");
            }
            else if (entry.Drive == new VolumeKey('U'))
            {
                seenU = true;
                Assert.AreEqual(0, BorrowCount(first), "first member's borrow was released before advancing to second");
                Assert.AreEqual(1, BorrowCount(second), "second member is borrowed while yielding its entries");
            }
        }

        Assert.IsTrue(seenT);
        Assert.IsTrue(seenU);
        Assert.AreEqual(0, BorrowCount(first));
        Assert.AreEqual(0, BorrowCount(second));

        // Early break on member 0 releases member 0's borrow and never touches member 1.
        var secondAdmitted = 0;
        second.Hooks.ReaderAdmitted = () => secondAdmitted++;
        foreach (var _ in set.Enumerate(new SearchQuery("readme.md", NameMatchMode.Exact), Token))
        {
            Assert.AreEqual(1, BorrowCount(first));
            break;
        }

        Assert.AreEqual(0, BorrowCount(first));
        Assert.AreEqual(0, BorrowCount(second));
        Assert.AreEqual(0, secondAdmitted);
    }

    [TestMethod]
    public async Task EnumerateRows_BorrowsOneMemberAtATimeAndSharesStateAcrossStructCopies()
    {
        using var fixture = new TwoDriveQueryFixture();
        await using var first = await fixture.OpenAsync();
        await using var second = await fixture.OpenSecondAsync();
        var set = new FileIndexSet([first, second]);

        var seenT = false;
        var seenU = false;
        foreach (var row in set.EnumerateRows(new SearchQuery("readme.md", NameMatchMode.Exact), Token))
        {
            if (row.Drive == new VolumeKey('T'))
            {
                seenT = true;
                Assert.AreEqual(1, BorrowCount(first));
                Assert.AreEqual(0, BorrowCount(second));
            }
            else if (row.Drive == new VolumeKey('U'))
            {
                seenU = true;
                Assert.AreEqual(0, BorrowCount(first));
                Assert.AreEqual(1, BorrowCount(second));
            }
        }

        Assert.IsTrue(seenT);
        Assert.IsTrue(seenU);
        Assert.AreEqual(0, BorrowCount(first));
        Assert.AreEqual(0, BorrowCount(second));

        // Struct copy taken while on member 0 still observes and disposes member 1's borrow after transition.
        var enumerable = set.EnumerateRows(new SearchQuery("readme.md", NameMatchMode.Exact), Token);
        using var enumerator = enumerable.GetEnumerator();
        Assert.IsTrue(enumerator.MoveNext());
        Assert.AreEqual(new VolumeKey('T'), enumerator.Current.Drive);
        using var copy = enumerator;

        // Advance original enumerator into member 1 ('U').
        while (enumerator.MoveNext() && enumerator.Current.Drive == new VolumeKey('T'))
        {
        }

        Assert.AreEqual(new VolumeKey('U'), enumerator.Current.Drive);
        Assert.AreEqual(0, BorrowCount(first));
        Assert.AreEqual(1, BorrowCount(second));

        // Disposing the copy taken earlier releases member 1's active borrow and invalidates both copies.
        copy.Dispose();
        Assert.AreEqual(0, BorrowCount(first));
        Assert.AreEqual(0, BorrowCount(second));

        AssertCurrentThrowsDisposed(enumerator);
        AssertMoveNextThrowsDisposed(enumerator);
    }

    [TestMethod]
    public async Task DisposingActiveMemberDuringSuspendedRowEnumeration_CancelsAndReleasesBorrow()
    {
        using var fixture = new TwoDriveQueryFixture();
        var first = await fixture.OpenAsync();
        await using var second = await fixture.OpenSecondAsync();
        var set = new FileIndexSet([first, second]);

        Task disposal;
        {
            var enumerable = set.EnumerateRows(new SearchQuery(null), Token);
            using var enumerator = enumerable.GetEnumerator();
            Assert.IsTrue(enumerator.MoveNext());
            Assert.AreEqual(1, BorrowCount(first));

            var cancellationDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.Hooks.LifecycleGatesTakenForDisposal = () => cancellationDelivered.TrySetResult();
            disposal = StartDisposalAndWaitForCancellation(first, cancellationDelivered.Task, Token);
            Assert.IsFalse(disposal.IsCompleted, "DisposeAsync waits for the suspended row enumerator's borrow");

            // Advancing after disposal began observes the linked disposal token.
            AssertDrainThrowsCancelled(enumerator);
        }

        await disposal;
    }

    static Task StartDisposalAndWaitForCancellation(FileIndex index, Task cancellationDelivered, CancellationToken token)
    {
        var disposal = index.DisposeAsync().AsTask();
        cancellationDelivered.WaitAsync(HangGuard, token).GetAwaiter().GetResult();
        return disposal;
    }

    static void AssertCurrentThrowsDisposed(IndexRowEnumerator enumerator)
    {
        try
        {
            _ = enumerator.Current;
            Assert.Fail("Expected ObjectDisposedException from Current.");
        }
        catch (ObjectDisposedException exception)
        {
            Assert.AreEqual(nameof(IndexRowEnumerator), exception.ObjectName);
        }
    }

    static void AssertMoveNextThrowsDisposed(IndexRowEnumerator enumerator)
    {
        try
        {
            _ = enumerator.MoveNext();
            Assert.Fail("Expected ObjectDisposedException from MoveNext.");
        }
        catch (ObjectDisposedException exception)
        {
            Assert.AreEqual(nameof(IndexRowEnumerator), exception.ObjectName);
        }
    }

    static void AssertDrainThrowsCancelled(IndexRowEnumerator enumerator)
    {
        try
        {
            while (enumerator.MoveNext())
            {
            }

            Assert.Fail("Expected OperationCanceledException from MoveNext.");
        }
        catch (OperationCanceledException)
        {
        }
    }
}
