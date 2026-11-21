using System.Collections;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.Index.InlineContentIndexes;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.ReadInlineContentAsync" /> observes its token before it checks the entries and at a bounded
///     cadence while it checks and maps them, so a long entry list never delays a cancellation by a whole pass.
/// </summary>
[TestClass]
public sealed class FileIndexInlineContentCancellationTests
{
    // More entries than one cancellation interval, so a pass that ignored the token would walk past the check.
    const int EntryCount = InlineContentPlan.CancellationCheckInterval + 904;

    /// <summary>Repeats one entry and counts every element read; it can cancel a token once a given read is reached.</summary>
    sealed class CountingEntries(FileEntry entry, int count, int cancelAtRead = -1,
        CancellationTokenSource? cancellation = null) : IReadOnlyList<FileEntry>
    {
        public int Reads { get; private set; }

        public int Count => count;

        public FileEntry this[int index]
        {
            get
            {
                if (++Reads == cancelAtRead)
                {
                    cancellation?.Cancel();
                }

                return entry;
            }
        }

        public IEnumerator<FileEntry> GetEnumerator() => throw new AssertFailedException("The read enumerated the entries.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [TestMethod]
    public async Task Read_WithAnAlreadyCancelledToken_ThrowsBeforeReadingAnyEntry()
    {
        var reader = new ScriptedInlineContentReader();
        await using var index = await OpenAsync(reader);
        var entries = new CountingEntries(Entry(index, 7), EntryCount);
        var calls = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            index.ReadInlineContentAsync(entries, _ => calls++, new CancellationToken(canceled: true)));

        Assert.AreEqual(0, entries.Reads, "no entry is read once the token is already cancelled");
        Assert.IsEmpty(reader.Calls);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task Read_CancelledWhileTheEntriesAreMapped_ThrowsAtTheNextCheckWithoutReading()
    {
        var reader = new ScriptedInlineContentReader();
        await using var index = await OpenAsync(reader);
        using var cancellation = new CancellationTokenSource();
        // The first pass checks every entry; the cancellation lands early in the second pass, the mapping.
        var entries = new CountingEntries(Entry(index, 7), EntryCount, cancelAtRead: EntryCount + 10, cancellation);
        var calls = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            index.ReadInlineContentAsync(entries, _ => calls++, cancellation.Token));

        Assert.AreEqual(EntryCount + InlineContentPlan.CancellationCheckInterval, entries.Reads,
            "the mapping stops at its next check instead of finishing its pass");
        Assert.IsEmpty(reader.Calls, "a cancelled plan reads nothing");
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task Read_CancelledWhileTheEntriesAreChecked_ThrowsAtTheNextCheck()
    {
        var reader = new ScriptedInlineContentReader();
        await using var index = await OpenAsync(reader);
        using var cancellation = new CancellationTokenSource();
        var entries = new CountingEntries(Entry(index, 7), EntryCount, cancelAtRead: 10, cancellation);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            index.ReadInlineContentAsync(entries, _ => { }, cancellation.Token));

        Assert.AreEqual(InlineContentPlan.CancellationCheckInterval, entries.Reads);
        Assert.IsEmpty(reader.Calls);
    }

    // A walked root has no reader, so the call only checks ownership and would report NotApplicable.
    [TestMethod]
    public async Task Read_OfAWalkedRootCancelledDuringTheOwnershipCheck_ThrowsInsteadOfNotApplicable()
    {
        using var directory = new TempDirectory("mftlib-inline-walk-cancel");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "a.txt"), "walked");
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = RootKey.FromDirectory(directory.Path),
            Producer = ProducerSelection.WalkOnly,
            CacheMode = CacheMode.Disabled
        }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        // Two reads check the entries; the cancellation lands on the last read of the ownership check.
        var entries = new CountingEntries(index.Search(new SearchQuery("a.txt")).Single(), 2, cancelAtRead: 4,
            cancellation);
        var calls = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            index.ReadInlineContentAsync(entries, _ => calls++, cancellation.Token));

        Assert.AreEqual(4, entries.Reads, "the ownership check finished its pass before the token was looked at");
        Assert.AreEqual(0, calls);
    }
}
