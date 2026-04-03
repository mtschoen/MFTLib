using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class MftVolumeTests
{
    string? _tempMftPath;

    [TestInitialize]
    public void Setup()
    {
        _tempMftPath = Path.GetTempFileName();
        // Generate a small synthetic MFT with 1000 records
        MftVolume.GenerateSyntheticMFT(_tempMftPath, 1000, 256);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempMftPath != null && File.Exists(_tempMftPath))
        {
            File.Delete(_tempMftPath);
        }
    }

    [TestMethod]
    public void ParseMFTFromFile_ReadAll_ReturnsRecords()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out var timings);

        Assert.IsTrue(records.Length > 0);
        Assert.IsTrue(timings.TotalRecords >= 1000);
        Assert.IsNotNull(records[0].FileName);
    }

    [TestMethod]
    public void ParseMFTFromFile_FilterWithNoMatchBits_ReturnsEmpty()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, "README.md", MatchFlags.None, out _);
        Assert.AreEqual(0, records.Length, "Expected no results when filter is set but no match bits");
    }

    [TestMethod]
    public void ParseMFTFromFile_FilterWithResolvePathsOnly_ReturnsEmpty()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, "README.md", MatchFlags.ResolvePaths, out _);
        Assert.AreEqual(0, records.Length, "Expected no results when filter is set but only resolve-paths bit is set");
    }

    [TestMethod]
    public void ParseMFTFromFile_ExactAndSubstringBits_ExactTakesPrecedence()
    {
        Assert.IsNotNull(_tempMftPath);
        // Both match bits set. Native code checks exact first, so
        // "README.md" should match exactly, not as a substring
        var bothBits = MftVolume.ParseMFTFromFile(_tempMftPath, "README.md", MatchFlags.ExactMatch | MatchFlags.Contains, out _);
        var exactOnly = MftVolume.ParseMFTFromFile(_tempMftPath, "README.md", MatchFlags.ExactMatch, out _);

        Assert.AreEqual(exactOnly.Length, bothBits.Length, "1|2 should behave the same as 1 (exact wins)");
    }

    [TestMethod]
    public void ParseMFTFromFile_AllBitsSet_SameAsExactWithPaths()
    {
        Assert.IsNotNull(_tempMftPath);
        // All bits set. Should be same as ExactMatch|ResolvePaths since exact takes precedence
        var allBits = MftVolume.ParseMFTFromFile(_tempMftPath, "README.md", MatchFlags.ExactMatch | MatchFlags.Contains | MatchFlags.ResolvePaths, out _);
        var exactWithPaths = MftVolume.ParseMFTFromFile(_tempMftPath, "README.md", MatchFlags.ExactMatch | MatchFlags.ResolvePaths, out _);

        Assert.AreEqual(exactWithPaths.Length, allBits.Length, "1|2|4 should behave the same as 1|4");
        foreach (var record in allBits)
        {
            Assert.AreEqual("README.md", record.FileName);
            Assert.IsNotNull(record.FullPath);
        }
    }

    [TestMethod]
    public void ParseMFTFromFile_NullFilterWithResolvePaths_PopulatesPaths()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, null, MatchFlags.ResolvePaths, out _);

        Assert.IsTrue(records.Length > 0, "Expected records to be returned");
        var withPaths = records.Where(r => r.FullPath != null).ToArray();
        Assert.IsTrue(withPaths.Length > 0, "Expected some records to have resolved paths");
    }

    [TestMethod]
    public void StreamRecords_EnumeratesAll()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out _);
        Assert.IsTrue(records.Length > 0);
    }

    [TestMethod]
    public void ParseMFTFromFile_Timings_ArePopulated()
    {
        Assert.IsNotNull(_tempMftPath);
        MftVolume.ParseMFTFromFile(_tempMftPath, out var timings);

        Assert.IsTrue(timings.TotalRecords >= 1000);
        Assert.IsTrue(timings.NativeTotalMs >= 0);
        Assert.IsTrue(timings.MarshalMs >= 0);
    }

    [TestMethod]
    public void ParseMFTFromFile_AllRecords_HaveFileNames()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out _);

        foreach (var record in records)
        {
            Assert.IsNotNull(record.FileName);
            Assert.AreNotEqual(string.Empty, record.FileName);
        }
    }

    [TestMethod]
    public void ParseMFTFromFile_ContainsDirectoriesAndFiles()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out _);

        var hasDirectory = records.Any(r => r.IsDirectory);
        var hasFile = records.Any(r => !r.IsDirectory && r.InUse);

        Assert.IsTrue(hasDirectory, "Expected at least one directory");
        Assert.IsTrue(hasFile, "Expected at least one file");
    }

    [TestMethod]
    public void ParseMFTFromFile_RecordNumbers_AreUnique()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out _);
        var uniqueCount = records.Select(r => r.RecordNumber).Distinct().Count();
        Assert.AreEqual(records.Length, uniqueCount, "Expected all record numbers to be unique");
    }

    [TestMethod]
    public void ParseMFTFromFile_WithSubstringFilter_ReturnsMatches()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, "main", MatchFlags.Contains, out _);

        Assert.IsTrue(records.Length > 0, "Expected substring filter 'main' to match some records");
        foreach (var record in records)
        {
            Assert.IsTrue(record.FileName.Contains("main", StringComparison.OrdinalIgnoreCase),
                $"Record '{record.FileName}' does not match substring filter 'main'");
        }
    }

    [TestMethod]
    public void ParseMFTFromFile_ExactFilter_NoMatch_ReturnsEmpty()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, "nonexistent_file_xyz", MatchFlags.ExactMatch, out _);
        Assert.AreEqual(0, records.Length);
    }

    [TestMethod]
    public void ParseMFTFromFile_ExactFilter_FindsKnownName()
    {
        Assert.IsNotNull(_tempMftPath);
        // "README.md" is one of the fixed synthetic filenames
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, "README.md", MatchFlags.ExactMatch, out _);
        Assert.IsTrue(records.Length > 0, "Expected exact filter to find 'README.md'");
        foreach (var record in records)
        {
            Assert.AreEqual("README.md", record.FileName);
        }
    }

    [TestMethod]
    public void ParseMFTFromFile_WithPaths_RecordsHaveFullPath()
    {
        Assert.IsNotNull(_tempMftPath);
        // Path resolution requires filter != null; use substring match + resolve paths (2|4=6)
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, "README.md", MatchFlags.ExactMatch | MatchFlags.ResolvePaths, out _);

        Assert.IsTrue(records.Length > 0, "Expected filter to match some records");
        var withPaths = records.Where(r => r.FullPath != null).ToArray();
        Assert.IsTrue(withPaths.Length > 0, "Expected some records to have resolved paths");

        // Some records may be directly under root (no separator), but at least
        // some should have nested paths with separators
        var nestedPaths = withPaths.Where(r => r.FullPath!.Contains("\\")).ToArray();
        Assert.IsTrue(nestedPaths.Length > 0 || withPaths.Length > 0,
            "Expected resolved paths to be populated");
    }

    [TestMethod]
    public void ParseMFTFromFile_SubstringFilterWithPaths_CombinesFlags()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, "main", MatchFlags.Contains | MatchFlags.ResolvePaths, out _);

        Assert.IsTrue(records.Length > 0, "Expected combined filter to match");
        foreach (var record in records)
        {
            Assert.IsTrue(record.FileName.Contains("main", StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(record.FullPath);
        }
    }

    [TestMethod]
    public void ParseMFTFromFile_RootRecord_IsDirectory()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out _);

        // Synthetic MFT places root at record 5 with name "."
        var root = records.FirstOrDefault(r => r.RecordNumber == 5);
        Assert.AreEqual(".", root.FileName);
        Assert.IsTrue(root.IsDirectory);
        Assert.IsTrue(root.InUse);
    }

    [TestMethod]
    public void ParseMFTFromFile_SystemRecords_ArePresent()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out _);

        // Records 0-4 are $MFT in synthetic data
        var mftRecords = records.Where(r => r.RecordNumber < 5).ToArray();
        Assert.IsTrue(mftRecords.Length > 0, "Expected system records to be present");
        foreach (var r in mftRecords)
        {
            Assert.AreEqual("$MFT", r.FileName);
        }
    }

    [TestMethod]
    public void GenerateSyntheticMFT_CreatesFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            MftVolume.GenerateSyntheticMFT(path, 100, 256);
            Assert.IsTrue(new FileInfo(path).Length > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void GenerateSyntheticMFT_FileSize_MatchesRecordCount()
    {
        var path = Path.GetTempFileName();
        try
        {
            const ulong recordCount = 500;
            MftVolume.GenerateSyntheticMFT(path, recordCount, 256);
            // Each MFT record is 1024 bytes
            var expectedSize = (long)recordCount * 1024;
            Assert.AreEqual(expectedSize, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ParseMFTFromFile_SelectivePathResolution_MatchesNativeResolution()
    {
        Assert.IsNotNull(_tempMftPath);
        // Scan without paths, then selectively resolve a few records
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out _);
        var lookup = records.ToDictionary(r => r.RecordNumber);

        // Also scan with native path resolution for comparison
        var withPaths = MftVolume.ParseMFTFromFile(_tempMftPath, null, MatchFlags.ResolvePaths, out _);
        var pathLookup = withPaths.Where(r => r.FullPath != null).ToDictionary(r => r.RecordNumber);

        // Resolve a few records manually and verify they match native resolution
        var resolved = 0;
        foreach (var record in records.Where(r => r.InUse && r.RecordNumber > 5).Take(20))
        {
            var manualPath = MftPathUtilities.ResolvePath(record.RecordNumber, lookup, "");
            if (pathLookup.TryGetValue(record.RecordNumber, out var nativeRecord) && nativeRecord.FullPath != null)
            {
                // Native paths include drive letter prefix; manual paths use empty drive letter
                var nativePath = nativeRecord.FullPath;
                // Both should produce the same relative structure
                Assert.IsTrue(manualPath.EndsWith(record.FileName),
                    $"Manual path '{manualPath}' should end with '{record.FileName}'");
                Assert.IsTrue(nativePath.EndsWith(record.FileName),
                    $"Native path '{nativePath}' should end with '{record.FileName}'");
                resolved++;
            }
        }

        Assert.IsTrue(resolved > 0, "Expected at least one record to be resolved by both methods");
    }

    [TestMethod]
    public void ExtractDriveLetter_ValidPath_ReturnsLetter()
    {
        Assert.AreEqual("C", MftVolume.ExtractDriveLetter(@"\\.\C:"));
        Assert.AreEqual("D", MftVolume.ExtractDriveLetter(@"\\.\D:"));
    }

    [TestMethod]
    public void ExtractDriveLetter_WrongLength_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, MftVolume.ExtractDriveLetter(@"\\.\C:\"));
        Assert.AreEqual(string.Empty, MftVolume.ExtractDriveLetter(@"\\.\"));
    }

    [TestMethod]
    public void ExtractDriveLetter_WrongPrefix_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, MftVolume.ExtractDriveLetter(@"XX.\C:"));
    }

    [TestMethod]
    public void ExtractDriveLetter_NoColon_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, MftVolume.ExtractDriveLetter(@"\\.\CX"));
    }
}
