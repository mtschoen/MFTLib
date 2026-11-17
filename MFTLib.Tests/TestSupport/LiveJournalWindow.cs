using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Reads the USN records a live test's own file operations wrote, from the cursor queried before
///     them up to, but not including, the cursor queried after them. NTFS writes each of those records before the operation
///     that caused it returns, so the read waits on no other process; a record whose timing another
///     process controls (the <c>FileDelete|Close</c> written when a scanner closes the last handle)
///     lies inside the window only when it was already written.
/// </summary>
static class LiveJournalWindow
{
    // Cancellation bound only, never an asserted duration: it bounds the watch delivering records that
    // were already written before it started.
    const int DeliverySeconds = 60;

    public static Task<List<UsnJournalEntry[]>> ReadAsync(MftVolume volume, UsnJournalCursor start,
        UsnJournalCursor end, CancellationToken cancellationToken)
    {
        return ReadAsync(volume.Journal.WatchUsnJournal(start, CancellationToken.None), start, end, cancellationToken);
    }

    public static async Task<List<UsnJournalEntry[]>> ReadAsync(
        IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> watch, UsnJournalCursor start,
        UsnJournalCursor end, CancellationToken cancellationToken)
    {
        var batches = new List<UsnJournalEntry[]>();
        var reachedEnd = false;
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(DeliverySeconds));
        // WatchUsnJournal ends quietly on cancellation, so the bound shows as the window never reaching its end.
        try
        {
            await foreach (var (batch, cursor) in watch.WithCancellation(bound.Token))
            {
                // The end is exclusive: a batch that runs past it keeps only the records written before it, so a
                // FileDelete written after the test's last operation never counts as inside the window.
                batches.Add(batch.Where(entry => entry.Usn < end.NextUsn).ToArray());
                if (cursor.NextUsn >= end.NextUsn)
                {
                    reachedEnd = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        Assert.IsTrue(reachedEnd,
            $"The watch did not deliver the journal window from USN {start.NextUsn} to {end.NextUsn} within "
            + $"{DeliverySeconds} s, although every record in it was written before the watch started. "
            + $"Batches delivered: {batches.Count}.");
        return batches;
    }
}
