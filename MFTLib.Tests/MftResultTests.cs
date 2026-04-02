using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib;

namespace MFTLib.Tests;

[TestClass]
public class MftResultTests
{
    private string? _tempMftPath;

    [TestInitialize]
    public void Setup()
    {
        _tempMftPath = Path.GetTempFileName();
        MftVolume.GenerateSyntheticMFT(_tempMftPath, 500, 256);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempMftPath != null && File.Exists(_tempMftPath))
            File.Delete(_tempMftPath);
    }

    [TestMethod]
    public void TotalRecords_ReturnsExpectedCount()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out var timings);
        Assert.AreEqual(500UL, timings.TotalRecords);
    }

    [TestMethod]
    public void UsedRecords_LessThanOrEqualToTotal()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out var timings);
        // UsedRecords excludes deleted/extension records
        Assert.IsTrue((ulong)records.Length <= timings.TotalRecords);
    }

    [TestMethod]
    public void ToArray_MaterializesRecords_StableStrings()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out _);

        // After ToArray, all records should have stable materialized strings
        foreach (var record in records)
        {
            var name1 = record.FileName;
            var name2 = record.FileName;
            Assert.AreEqual(name1, name2);
            Assert.IsNotNull(name1);
        }
    }

    [TestMethod]
    public void ToArray_WithPaths_MaterializesFullPaths()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, null, 4, out _);

        var withPaths = records.Where(r => r.FullPath != null).ToArray();
        Assert.IsTrue(withPaths.Length > 0);

        // FileName should be extractable from FullPath for path-resolved records
        foreach (var record in withPaths.Take(20))
        {
            var pathFileName = record.FullPath!.Contains('\\')
                ? record.FullPath[(record.FullPath.LastIndexOf('\\') + 1)..]
                : record.FullPath;
            Assert.AreEqual(pathFileName, record.FileName,
                $"FileName '{record.FileName}' doesn't match end of FullPath '{record.FullPath}'");
        }
    }
}
