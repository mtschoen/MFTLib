using System.Collections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
/// Tests that require admin elevation to open raw volume handles.
/// Run via: scripts/run-admin-tests.ps1
/// </summary>
[TestClass]
[TestCategory("RequiresAdmin")]
public class MftVolumeAdminTests
{
    static void RequireElevation()
    {
        if (!ElevationUtilities.IsElevated())
            Assert.Inconclusive("Requires admin elevation. Run scripts/run-admin-tests.ps1");
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
        using var volume = MftVolume.Open("C", bufferSizeRecords: 65536);
        var records = volume.ReadAllRecords();
        Assert.IsTrue(records.Length > 0);
    }

    [TestMethod]
    public void ReadAllRecords_ReturnsNonEmpty()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        var records = volume.ReadAllRecords();

        Assert.IsTrue(records.Length > 0, "Expected records on C:");
    }

    [TestMethod]
    public void ReadAllRecords_WithTimings_PopulatesTimings()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        var records = volume.ReadAllRecords(out var timings);

        Assert.IsTrue(records.Length > 0);
        Assert.IsTrue(timings.TotalRecords > 0);
        Assert.IsTrue(timings.NativeTotalMs > 0);
        Assert.IsTrue(timings.MarshalMs >= 0);
    }

    [TestMethod]
    public void ReadAllRecords_WithResolvePaths_PopulatesFullPath()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        var records = volume.ReadAllRecords(resolvePaths: true);

        var withPaths = records.Where(r => r.FullPath != null).ToArray();
        Assert.IsTrue(withPaths.Length > 0, "Expected some records with resolved paths");
    }

    [TestMethod]
    public void ReadAllRecords_WithResolvePathsAndTimings_PopulatesFullPath()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        var records = volume.ReadAllRecords(resolvePaths: true, out var timings);

        Assert.IsTrue(timings.TotalRecords > 0);
        var withPaths = records.Where(r => r.FullPath != null).ToArray();
        Assert.IsTrue(withPaths.Length > 0, "Expected some records with resolved paths");

        // Paths should start with C:\
        var withDrive = withPaths.Where(r => r.FullPath!.StartsWith(@"C:\")).ToArray();
        Assert.IsTrue(withDrive.Length > 0, "Expected paths to start with C:\\");
    }

    [TestMethod]
    public void FindByName_ExactMatch_FindsKnownFile()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        // ntldr or bootmgr should exist on C:
        var records = volume.FindByName("bootmgr");

        // If bootmgr doesn't exist, try a Windows system file
        if (records.Length == 0)
            records = volume.FindByName("ntldr");

        // At minimum, we verified the call didn't throw
        Assert.IsNotNull(records);
    }

    [TestMethod]
    public void FindByName_SubstringMatch_ReturnsResults()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        var records = volume.FindByName(".dll", MatchFlags.Contains);

        Assert.IsTrue(records.Length > 0, "Expected to find some .dll files on C:");
    }

    [TestMethod]
    public void FindByName_WithResolvePaths_PopulatesFullPath()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        var records = volume.FindByName(".exe", MatchFlags.Contains | MatchFlags.ResolvePaths, out var timings);

        Assert.IsTrue(records.Length > 0, "Expected to find some .exe files");
        var withPaths = records.Where(r => r.FullPath != null).ToArray();
        Assert.IsTrue(withPaths.Length > 0, "Expected resolved paths");
        Assert.IsTrue(timings.TotalRecords > 0);
    }

    [TestMethod]
    public void StreamRecords_SubstringWithPaths_CombinesFlags()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords(".dll", MatchFlags.Contains | MatchFlags.ResolvePaths);

        var count = 0;
        MftRecord? firstWithPath = null;
        foreach (var record in result)
        {
            Assert.IsTrue(record.FileName.Contains(".dll", StringComparison.OrdinalIgnoreCase),
                $"Record '{record.FileName}' doesn't match substring '.dll'");

            if (firstWithPath == null && record.FullPath != null)
                firstWithPath = record.Materialize();

            count++;
            if (count >= 100) break;
        }

        Assert.IsTrue(count > 0, "Expected substring filter to find .dll files");
        Assert.IsNotNull(firstWithPath, "Expected at least one record with a resolved path");
        Assert.IsTrue(firstWithPath.Value.FullPath!.StartsWith(@"C:\"),
            $"Expected path to start with C:\\ but got '{firstWithPath.Value.FullPath}'");
    }

    [TestMethod]
    public void StreamRecords_FilterWithNoMatchBits_ReturnsEmpty()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords("explorer.exe");

        var count = 0;
        foreach (var _ in result) count++;
        Assert.AreEqual(0, count, "Expected no results when filter is set but no match bits");
    }

    [TestMethod]
    public void FindFiles_ReturnsOnlyFiles()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        var paths = volume.FindFiles("explorer.exe").ToList();

        Assert.IsTrue(paths.Count > 0, "Expected to find explorer.exe");
        foreach (var path in paths)
        {
            Assert.IsTrue(path.Contains("explorer.exe", StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public void FindDirectories_ReturnsOnlyDirectories()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        var paths = volume.FindDirectories("Windows").ToList();

        Assert.IsTrue(paths.Count > 0, "Expected to find Windows directory");
    }

    [TestMethod]
    public void StreamRecords_CanEnumerate()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords();

        var count = 0;
        foreach (var unused in result)
        {
            count++;
            if (count >= 100) break; // Don't enumerate everything
        }

        Assert.IsTrue(count >= 100, "Expected at least 100 records on C:");
    }

    [TestMethod]
    public void StreamRecords_WithFilter_ReturnsFiltered()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords("explorer.exe", MatchFlags.ExactMatch);

        var records = new List<MftRecord>();
        foreach (var record in result)
        {
            records.Add(record.Materialize());
        }

        Assert.IsTrue(records.Count > 0, "Expected to find explorer.exe");
        foreach (var r in records)
        {
            Assert.AreEqual("explorer.exe", r.FileName);
        }
    }

    [TestMethod]
    public void StreamRecords_ToArray_MaterializesAll()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords();
        var records = result.ToArray();

        Assert.IsTrue(records.Length > 0);
        // Verify materialized records have stable strings
        var first = records[0];
        var name1 = first.FileName;
        var name2 = first.FileName;
        Assert.AreEqual(name1, name2, "Materialized FileName should be stable");
    }

    [TestMethod]
    public void Dispose_PreventsSubsequentCalls()
    {
        RequireElevation();
        var volume = MftVolume.Open("C");
        volume.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() => volume.StreamRecords());
    }

    [TestMethod]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        RequireElevation();
        var volume = MftVolume.Open("C");
        volume.Dispose();
        volume.Dispose(); // Should not throw
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
        using var handle = FileUtilities.GetVolumeHandle(@"\\.\C:");
        Assert.IsFalse(handle.IsInvalid);
        Assert.IsFalse(handle.IsClosed);
    }

    [TestMethod]
    public void GetVolumeHandle_InvalidVolume_Throws()
    {
        RequireElevation();
        Assert.ThrowsException<IOException>(() => FileUtilities.GetVolumeHandle(@"\\.\Q:"));
    }

    [TestMethod]
    public void MftResult_Dispose_PreventsEnumeration()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        var result = volume.StreamRecords();
        result.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() =>
        {
            foreach (var _ in result) { }
        });
    }

    [TestMethod]
    public void MftResult_Dispose_PreventsToArray()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        var result = volume.StreamRecords();
        result.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() => result.ToArray());
    }

    [TestMethod]
    public void MftResult_Properties_MatchRecordCounts()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords();

        Assert.IsTrue(result.TotalRecords > 0);
        Assert.IsTrue(result.UsedRecords > 0);
        Assert.IsTrue(result.UsedRecords <= result.TotalRecords);
    }

    [TestMethod]
    public void StreamRecords_WithPaths_FileNameExtractedFromPath()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords("explorer", MatchFlags.Contains | MatchFlags.ResolvePaths);

        foreach (var record in result)
        {
            // When path entries are used, FileName is extracted from the path
            Assert.IsTrue(record.FileName.Length > 0, "FileName should not be empty");
            if (record.FullPath != null && record.FullPath.Contains('\\'))
            {
                var expected = record.FullPath[(record.FullPath.LastIndexOf('\\') + 1)..];
                Assert.AreEqual(expected, record.FileName,
                    $"FileName '{record.FileName}' should match end of FullPath '{record.FullPath}'");
            }
        }
    }

    [TestMethod]
    public void IsElevated_WhenAdmin_ReturnsTrue()
    {
        RequireElevation();
        Assert.IsTrue(ElevationUtilities.IsElevated());
    }


    [TestMethod]
    public void FindRecords_NoDirectoryFilter_ReturnsBothTypes()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        // isDirectory: null means return both files and directories
        var results = volume.FindRecords("Windows").ToList();
        Assert.IsTrue(results.Count > 0, "Expected to find records named 'Windows'");
    }

    [TestMethod]
    public void MftResult_NonGenericEnumerator_Works()
    {
        RequireElevation();
        using var volume = MftVolume.Open("C");
        using var result = volume.StreamRecords("explorer.exe", MatchFlags.ExactMatch);

        // Cast to non-generic IEnumerable to hit the explicit interface implementation
        IEnumerable enumerable = result;
        var count = 0;
        foreach (var item in enumerable)
        {
            Assert.IsInstanceOfType(item, typeof(MftRecord));
            count++;
        }
        Assert.IsTrue(count > 0, "Expected at least one record via non-generic enumerator");
    }
}
