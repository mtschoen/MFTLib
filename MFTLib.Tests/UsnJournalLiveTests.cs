using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class UsnJournalLiveTests
{
    static bool IsAdmin() => ElevationUtilities.IsElevated();

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void QueryUsnJournal_OnRealVolume_ReturnsCursor()
    {
        if (!IsAdmin()) { Assert.Inconclusive("Requires admin"); return; }

        using var volume = MftVolume.Open("C");
        var cursor = volume.QueryUsnJournal();

        Assert.IsTrue(cursor.JournalId > 0, "JournalId should be nonzero");
        Assert.IsTrue(cursor.NextUsn > 0, "NextUsn should be positive");
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void ReadUsnJournal_AfterTempFileCreate_ContainsEntry()
    {
        if (!IsAdmin()) { Assert.Inconclusive("Requires admin"); return; }

        using var volume = MftVolume.Open("C");

        // Get current cursor
        var cursor = volume.QueryUsnJournal();

        // Create and delete a temp file to generate journal entries
        var tempPath = Path.Combine(Path.GetTempPath(), $"mftlib-usn-test-{Guid.NewGuid()}.tmp");
        File.WriteAllText(tempPath, "usn test");
        var tempFileName = Path.GetFileName(tempPath);

        try
        {
            // Read journal since our cursor
            var (entries, updatedCursor) = volume.ReadUsnJournal(cursor);

            Assert.IsTrue(entries.Length > 0, "Should have at least one journal entry");
            Assert.IsTrue(updatedCursor.NextUsn >= cursor.NextUsn, "Cursor should advance");

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
        if (!IsAdmin()) { Assert.Inconclusive("Requires admin"); return; }

        using var volume = MftVolume.Open("C");
        var cursor = volume.QueryUsnJournal();

        // Reading from current position should return very few entries
        var (entries, _) = volume.ReadUsnJournal(cursor);

        Assert.IsTrue(entries.Length < 1000,
            $"Expected few entries from current position, got {entries.Length}");
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public async Task WatchUsnJournal_DetectsNewFile()
    {
        if (!IsAdmin()) { Assert.Inconclusive("Requires admin"); return; }

        using var volume = MftVolume.Open("C");
        var cursor = volume.QueryUsnJournal();

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tempPath = Path.Combine(Path.GetTempPath(), $"mftlib-watch-test-{Guid.NewGuid()}.tmp");
        var tempFileName = Path.GetFileName(tempPath);

        var batches = new List<UsnJournalEntry[]>();
        var watchTask = Task.Run(async () =>
        {
            // ReSharper disable once AccessToDisposedClosure
            // volume is captured inside Task.Run, but watchTask is awaited before the enclosing using exits,
            // so volume remains alive for the full duration of the task.
            await foreach (var batch in volume.WatchUsnJournal(cursor, cancellationTokenSource.Token))
            {
                batches.Add(batch);
                if (batch.Any(e => e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase)))
                    break;
            }
        });

        await Task.Delay(200);
        File.WriteAllText(tempPath, "watch test");

        try
        {
            await watchTask;
        }
        catch (OperationCanceledException)
        {
            // Timeout is acceptable
        }
        finally
        {
            File.Delete(tempPath);
        }

        var allEntries = batches.SelectMany(b => b).ToArray();
        Assert.IsTrue(allEntries.Any(e => e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase)),
            $"Should find {tempFileName} in watched entries");
    }
}
