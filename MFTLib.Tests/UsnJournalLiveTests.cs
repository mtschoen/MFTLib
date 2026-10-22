using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class UsnJournalLiveTests
{
    static bool IsAdmin()
    {
        return ElevationUtilities.DefaultProvider.IsElevated();
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void QueryUsnJournal_OnRealVolume_ReturnsCursor()
    {
        if (!IsAdmin())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        using var volume = MftVolume.Open('C');
        var cursor = volume.Journal.QueryUsnJournalCursor();

        Assert.IsGreaterThan(0UL, cursor.JournalIdentifier, "JournalIdentifier should be nonzero");
        Assert.IsGreaterThan(0L, cursor.NextUsn, "NextUsn should be positive");
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void ReadUsnJournal_AfterTempFileCreate_ContainsEntry()
    {
        if (!IsAdmin())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        using var volume = MftVolume.Open('C');

        // Get current cursor
        var cursor = volume.Journal.QueryUsnJournalCursor();

        // Create and delete a temp file to generate journal entries
        var tempPath = Path.Combine(Path.GetTempPath(), $"mftlib-usn-test-{Guid.NewGuid()}.tmp");
        File.WriteAllText(tempPath, "usn test");
        var tempFileName = Path.GetFileName(tempPath);

        try
        {
            // Read journal since our cursor
            var (entries, updatedCursor) = volume.Journal.ReadUsnJournalBounded(cursor, 0);

            Assert.IsNotEmpty(entries, "Should have at least one journal entry");
            Assert.IsGreaterThanOrEqualTo(cursor.NextUsn, updatedCursor.NextUsn, "Cursor should advance");

            // Find our temp file in the entries
            var found = entries.Any(e => e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(found, $"Should find {tempFileName} in USN journal entries");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void ReadUsnJournal_CurrentPosition_ReturnsEmptyOrFew()
    {
        if (!IsAdmin())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        using var volume = MftVolume.Open('C');
        var cursor = volume.Journal.QueryUsnJournalCursor();

        // Reading from current position should return very few entries
        var (entries, _) = volume.Journal.ReadUsnJournalBounded(cursor, 0);

        Assert.IsLessThan(1000, entries.Length,
            $"Expected few entries from current position, got {entries.Length}");
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public async Task WatchUsnJournal_DetectsNewFile()
    {
        if (!IsAdmin())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        using var volume = MftVolume.Open('C');
        var cursor = volume.Journal.QueryUsnJournalCursor();

        var tempPath = Path.Combine(Path.GetTempPath(), $"mftlib-watch-test-{Guid.NewGuid()}.tmp");
        var tempFileName = Path.GetFileName(tempPath);

        var batches = new List<UsnJournalEntry[]>();
        var watchTask = Task.Run(async () =>
        {
            using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(10));
            // ReSharper disable once AccessToDisposedClosure
            // volume is captured from outer scope; watchTask is awaited before volume's using exits.
            await foreach (var (batch, _) in volume.Journal.WatchUsnJournal(cursor, cancellationTokenSource.Token))
            {
                batches.Add(batch);
                if (batch.Any(e => e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase)))
                {
                    break;
                }
            }
        }, TestContext.CancellationToken);

        await File.WriteAllTextAsync(tempPath, "watch test", CancellationToken.None);

        try
        {
            await watchTask;
        }
        catch (OperationCanceledException) when (!TestContext.CancellationToken.IsCancellationRequested)
        {
            // Timeout is acceptable
        }
        finally
        {
            File.Delete(tempPath);
        }

        var allEntries = batches.SelectMany(b => b).ToArray();
        Assert.Contains(e => e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase), allEntries,
            $"Should find {tempFileName} in watched entries");
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void GrowUsnJournal_OnRealVolume_GrowsAndReadsBack()
    {
        if (!IsAdmin())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        using var volume = MftVolume.Open('C');
        var before = volume.Journal.QueryUsnJournalSettings();
        if (before.MaximumSize >= 1024L * 1024 * 1024)
        {
            // Bound the cumulative growth across repeated runs: this test mutates a
            // persistent, shared OS resource and cannot restore the old size (shrink is
            // refused by design), so it opts out once the journal is already large.
            Assert.Inconclusive("Journal maximum is already at or above 1 GB");
            return;
        }

        var targetMaximum = before.MaximumSize + before.AllocationDelta;
        var grown = volume.Journal.GrowUsnJournal(targetMaximum, before.AllocationDelta);

        Assert.IsGreaterThanOrEqualTo(targetMaximum, grown.MaximumSize,
            $"Expected at least {targetMaximum} bytes, got {grown.MaximumSize}");

        // Grow-only refusal, using the just-grown size (no second mutation).
        InvalidOperationException? caught = null;
        try
        {
            volume.Journal.GrowUsnJournal(grown.MaximumSize, grown.AllocationDelta);
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        Assert.IsNotNull(caught);
        Assert.Contains("only growth", caught.Message);
    }

    public TestContext TestContext { get; set; } = null!;
}
