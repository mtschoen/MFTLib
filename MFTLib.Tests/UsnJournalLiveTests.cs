using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
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
        using var temporaryFile = new TempFile("mftlib-usn-test", create: false);
        var tempPath = temporaryFile.Path;
        File.WriteAllText(tempPath, "usn test");
        var tempFileName = Path.GetFileName(tempPath);

        // Read journal since our cursor
        var (entries, updatedCursor) = volume.Journal.ReadUsnJournalBounded(cursor, 0);

        Assert.IsNotEmpty(entries, "Should have at least one journal entry");
        Assert.IsGreaterThanOrEqualTo(cursor.NextUsn, updatedCursor.NextUsn, "Cursor should advance");

        // Find our temp file in the entries
        var found = entries.Any(e => e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(found, $"Should find {tempFileName} in USN journal entries");
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

        using var temporaryFile = new TempFile("mftlib-watch-test", create: false);
        var tempPath = temporaryFile.Path;
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

        var allEntries = batches.SelectMany(b => b).ToArray();
        Assert.Contains(e => e.FileName.Equals(tempFileName, StringComparison.OrdinalIgnoreCase), allEntries,
            $"Should find {tempFileName} in watched entries");
    }

    // Each grow test mounts a throwaway 4 GiB dynamically expanding NTFS VHDX (see
    // TempNtfsVhdx for the sizing). Five percent of the volume rounded down to a multiple of
    // 1 MiB gives a recommended maximum of 204 MiB; the journal is seeded at 64 MiB with an
    // 8 MiB delta, so the below-cap grow targets 72 MiB and the above-cap request is the cap
    // plus 32 MiB. No persistent host volume is touched.

    static void AssertGrownSettings(
        UsnJournalSettings before,
        long effectiveTarget,
        UsnJournalSettings grown,
        UsnJournalSettings readBack)
    {
        const long oneMiB = 1024L * 1024;
        Assert.IsGreaterThan(before.MaximumSize, grown.MaximumSize,
            $"Grown journal size {grown.MaximumSize} should exceed pre-call size {before.MaximumSize}.");
        Assert.IsLessThanOrEqualTo(effectiveTarget, grown.MaximumSize,
            $"Grown journal size {grown.MaximumSize} should not exceed effective target {effectiveTarget}.");
        Assert.IsGreaterThanOrEqualTo(effectiveTarget - oneMiB, grown.MaximumSize,
            $"Grown journal size {grown.MaximumSize} should be at least effective target minus 1 MiB ({effectiveTarget - oneMiB}).");
        Assert.AreEqual(before.AllocationDelta, grown.AllocationDelta);
        Assert.AreEqual(grown, readBack);
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void GrowUsnJournal_OnRealVolume_WithinMaximumRecommended_GrowsAsRequestedAndReadsBack()
    {
        if (!IsAdmin())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        using var vhdx = TempNtfsVhdx.Create();
        using var volume = MftVolume.Open(vhdx.Volume);
        var before = volume.Journal.QueryUsnJournalSettings();
        var maximumRecommended = UsnJournalSettings.QueryMaximumRecommendedSize(vhdx.Volume);
        var requestedMaximum = before.MaximumSize + before.AllocationDelta;

        Assert.IsLessThan(maximumRecommended, requestedMaximum,
            $"Target size {requestedMaximum} should sit below the 5 percent recommended cap {maximumRecommended}.");

        var grown = volume.Journal.GrowUsnJournal(requestedMaximum, before.AllocationDelta);
        var readBack = volume.Journal.QueryUsnJournalSettings();

        Assert.AreEqual(UsnJournalGrowthOutcome.GrewAsRequested, grown.Outcome);
        Assert.AreEqual(requestedMaximum, grown.RequestedMaximumSize);
        Assert.AreEqual(maximumRecommended, grown.MaximumRecommendedSize);
        AssertGrownSettings(before, requestedMaximum, grown.JournalSettings, readBack);
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void GrowUsnJournal_OnRealVolume_AboveMaximumRecommendedWithoutOverride_ClampsAndReadsBack()
    {
        if (!IsAdmin())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        using var vhdx = TempNtfsVhdx.Create();
        using var volume = MftVolume.Open(vhdx.Volume);
        var before = volume.Journal.QueryUsnJournalSettings();
        var maximumRecommended = UsnJournalSettings.QueryMaximumRecommendedSize(vhdx.Volume);
        var requestedMaximum = maximumRecommended + (4 * before.AllocationDelta);

        Assert.IsLessThan(maximumRecommended, before.MaximumSize,
            $"Initial journal maximum {before.MaximumSize} should sit below the recommended cap {maximumRecommended}.");

        var grown = volume.Journal.GrowUsnJournal(requestedMaximum, before.AllocationDelta);
        var readBack = volume.Journal.QueryUsnJournalSettings();

        Assert.AreEqual(UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended, grown.Outcome);
        Assert.AreEqual(requestedMaximum, grown.RequestedMaximumSize);
        Assert.AreEqual(maximumRecommended, grown.MaximumRecommendedSize);
        Assert.IsLessThan(requestedMaximum, grown.JournalSettings.MaximumSize,
            $"Clamped journal size {grown.JournalSettings.MaximumSize} should remain below the unclamped request {requestedMaximum}.");
        AssertGrownSettings(before, maximumRecommended, grown.JournalSettings, readBack);
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void GrowUsnJournal_OnRealVolume_AboveMaximumRecommendedWithOverride_GrowsToRequestedAndReadsBack()
    {
        if (!IsAdmin())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        using var vhdx = TempNtfsVhdx.Create();
        using var volume = MftVolume.Open(vhdx.Volume);
        var before = volume.Journal.QueryUsnJournalSettings();
        var maximumRecommended = UsnJournalSettings.QueryMaximumRecommendedSize(vhdx.Volume);
        var requestedMaximum = maximumRecommended + (4 * before.AllocationDelta);

        var grown = volume.Journal.GrowUsnJournal(
            requestedMaximum,
            before.AllocationDelta,
            allowAboveMaximumRecommendedSize: true);
        var readBack = volume.Journal.QueryUsnJournalSettings();

        Assert.AreEqual(UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride, grown.Outcome);
        Assert.AreEqual(requestedMaximum, grown.RequestedMaximumSize);
        Assert.AreEqual(maximumRecommended, grown.MaximumRecommendedSize);
        AssertGrownSettings(before, requestedMaximum, grown.JournalSettings, readBack);
    }

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public void GrowUsnJournal_OnRealVolume_AtOrBelowCurrentMaximum_ReturnsAlreadyAtOrAboveTargetAndReadsBack()
    {
        if (!IsAdmin())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        using var vhdx = TempNtfsVhdx.Create();
        using var volume = MftVolume.Open(vhdx.Volume);
        var before = volume.Journal.QueryUsnJournalSettings();
        var maximumRecommended = UsnJournalSettings.QueryMaximumRecommendedSize(vhdx.Volume);

        var unchanged = volume.Journal.GrowUsnJournal(before.MaximumSize, before.AllocationDelta);
        var readBack = volume.Journal.QueryUsnJournalSettings();

        Assert.AreEqual(UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget, unchanged.Outcome);
        Assert.AreEqual(before.MaximumSize, unchanged.RequestedMaximumSize);
        Assert.AreEqual(maximumRecommended, unchanged.MaximumRecommendedSize);
        Assert.AreEqual(before, unchanged.JournalSettings);
        Assert.AreEqual(before, readBack);
    }

    public TestContext TestContext { get; set; } = null!;
}
