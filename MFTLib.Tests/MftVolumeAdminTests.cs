using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
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
    public void Open_ValidRoot_Succeeds()
    {
        RequireElevation();
        using var volume = MftVolume.Open('C');
        Assert.IsNotNull(volume);
    }

    [TestMethod]
    public void Open_WithCustomBufferSize_Succeeds()
    {
        RequireElevation();
        using var volume = MftVolume.Open('C', 65536);
        var records = volume.ReadAll();
        Assert.IsNotEmpty(records);
    }

    [TestMethod]
    public void ReadAll_ReturnsNonEmpty()
    {
        RequireElevation();
        using var volume = MftVolume.Open('C');
        var records = volume.ReadAll();

        Assert.IsNotEmpty(records, "Expected records on C:");
    }

    [TestMethod]
    public void ReadAll_MaterializesRecordsWithStableStrings()
    {
        RequireElevation();
        using var volume = MftVolume.Open('C');
        var records = volume.ReadAll();

        Assert.IsNotEmpty(records);
        var first = records[0];
        var name1 = first.FileName;
        var name2 = first.FileName;
        Assert.AreEqual(name1, name2, "Materialized FileName should be stable");
    }

    [TestMethod]
    public void Open_InvalidVolume_Throws()
    {
        RequireElevation();
        Assert.ThrowsExactly<IOException>(() => MftVolume.Open('Q'));
    }

    [TestMethod]
    public void GetVolumeHandle_ValidVolume_ReturnsValidHandle()
    {
        RequireElevation();
        using var handle = FileUtilities.Hooks._getVolumeHandle(@"\\.\C:");
        Assert.IsFalse(handle.IsInvalid);
        Assert.IsFalse(handle.IsClosed);
    }

    [TestMethod]
    public void GetVolumeHandle_InvalidVolume_Throws()
    {
        RequireElevation();
        Assert.ThrowsExactly<IOException>(() => FileUtilities.Hooks._getVolumeHandle(@"\\.\Q:"));
    }

    [TestMethod]
    public void ReadRecordBatches_RealVolume_TakesTwoBatches_DisposesEarly()
    {
        RequireElevation();
        using var volume = MftVolume.Open('C');
        var batches = new List<MftRecord[]>();
        foreach (var batch in volume.ReadRecordBatches(100, null, null, CancellationToken.None))
        {
            batches.Add(batch);
            if (batches.Count == 2)
            {
                break;
            }
        }

        Assert.HasCount(2, batches);
        Assert.HasCount(100, batches[0]);
        Assert.HasCount(100, batches[1]);
        Assert.IsNotNull(batches[0][0].FileName);
        Assert.IsNotNull(batches[1][0].FileName);
    }
}
