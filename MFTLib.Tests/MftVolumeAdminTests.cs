using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Tests that require admin elevation to open raw volume handles.
///     Run via: scripts/run-coverage.ps1
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("RequiresAdmin")]
public class MftVolumeAdminTests
{
    static void RequireElevation()
    {
        if (!ElevationUtilities.DefaultProvider.IsElevated())
        {
            Assert.Inconclusive("Requires admin elevation. Run scripts/run-coverage.ps1");
        }
    }

    [TestMethod]
    public void Open_ValidDriveLetter_Succeeds()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        Assert.IsNotNull(volume);
    }

    [TestMethod]
    public void Open_WithColon_Succeeds()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C:");
        Assert.IsNotNull(volume);
    }

    [TestMethod]
    public void Open_WithBackslash_Succeeds()
    {
        RequireElevation();
        using var volume = MftVolume.Open(@"C:\");
        Assert.IsNotNull(volume);
    }

    [TestMethod]
    public void Open_WithCustomBufferSize_Succeeds()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C", 65536);
        var records = volume.ReadAll();
        Assert.IsTrue(records.Length > 0);
    }

    [TestMethod]
    public void ReadAllRecords_ReturnsNonEmpty()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        var records = volume.ReadAll();

        Assert.IsTrue(records.Length > 0, "Expected records on C:");
    }

    [TestMethod]
    public void ReadAllRecords_WithTimings_PopulatesTimings()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        var records = volume.ReadAll(out var timings, out var totalRecords);

        Assert.IsTrue(records.Length > 0);
        Assert.IsTrue(totalRecords > 0);
        Assert.IsTrue(timings.NativeTotal > TimeSpan.Zero);
    }

    [TestMethod]
    public void StreamRecords_CanEnumerate()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords(false, null, null, CancellationToken.None);

        var count = 0;
        foreach (var unused in result)
        {
            count++;
            if (count >= 100)
            {
                break; // Don't enumerate everything
            }
        }

        Assert.IsTrue(count >= 100, "Expected at least 100 records on C:");
    }

    [TestMethod]
    public void StreamRecords_ToArray_MaterializesAll()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords(false, null, null, CancellationToken.None);
        var records = result.ToArray();

        Assert.IsTrue(records.Length > 0);
        // Verify materialized records have stable strings
        var first = records[0];
        var name1 = first.FileName;
        var name2 = first.FileName;
        Assert.AreEqual(name1, name2, "Materialized FileName should be stable");
    }

    [TestMethod]
    public void Open_InvalidVolume_Throws()
    {
        RequireElevation();
        Assert.ThrowsException<IOException>(() => MftVolume.Open("Q"));
    }

    [TestMethod]
    public void GetVolumeHandle_ValidVolume_ReturnsValidHandle()
    {
        RequireElevation();
        using var handle = FileUtilities._getVolumeHandle(@"\\.\C:");
        Assert.IsFalse(handle.IsInvalid);
        Assert.IsFalse(handle.IsClosed);
    }

    [TestMethod]
    public void GetVolumeHandle_InvalidVolume_Throws()
    {
        RequireElevation();
        Assert.ThrowsException<IOException>(() => FileUtilities._getVolumeHandle(@"\\.\Q:"));
    }

    [TestMethod]
    public void MftResult_Properties_MatchRecordCounts()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords(false, null, null, CancellationToken.None);

        Assert.IsTrue(result.TotalRecords > 0);
        Assert.IsTrue(result.UsedRecords > 0);
        Assert.IsTrue(result.UsedRecords <= result.TotalRecords);
    }

    [TestMethod]
    public void ReadRecordBatches_RealVolume_TakesTwoBatches_DisposesEarly()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        var batches = new List<MftRecord[]>();
        foreach (var batch in volume.ReadRecordBatches(100, null, null, CancellationToken.None))
        {
            batches.Add(batch);
            if (batches.Count == 2)
            {
                break;
            }
        }

        Assert.AreEqual(2, batches.Count);
        Assert.AreEqual(100, batches[0].Length);
        Assert.AreEqual(100, batches[1].Length);
        Assert.IsNotNull(batches[0][0].FileName);
        Assert.IsNotNull(batches[1][0].FileName);
    }
}
