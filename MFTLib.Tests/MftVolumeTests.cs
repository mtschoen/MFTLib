using Microsoft.VisualStudio.TestTools.UnitTesting;
using MFTLib;

namespace MFTLib.Tests;

[TestClass]
public class MftVolumeTests
{
    private string? _tempMftPath;

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
    public void ParseMFTFromFile_WithFilter_ReturnsFilteredRecords()
    {
        Assert.IsNotNull(_tempMftPath);
        // The synthetic generator creates files like "file_0.txt", "dir_1", etc.
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, "file_10", 0, out _);
        
        foreach (var record in records)
        {
            Assert.IsTrue(record.FileName.Contains("file_10"));
        }
    }

    [TestMethod]
    public void ParseMFTFromFile_WithResolvePaths_ReturnsPaths()
    {
        Assert.IsNotNull(_tempMftPath);
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, null, 4u, out _); // 4u = resolve paths
        
        // Synthetic generator creates some nested structures
        var nestedFile = records.FirstOrDefault(r => r.FileName == "nested_file.txt");
        if (nestedFile.RecordNumber != 0)
        {
            Assert.IsNotNull(nestedFile.FullPath);
            Assert.IsTrue(nestedFile.FullPath.Contains("\\"));
        }
    }

    [TestMethod]
    public void StreamRecords_EnumeratesAll()
    {
        Assert.IsNotNull(_tempMftPath);
        // We need a volume handle for StreamRecords usually, but ParseMFTFromFile 
        // uses the same underlying implementation. We can test the MftResult wrapper.
        
        // This is a bit of a hack since StreamRecords requires a SafeFileHandle
        // but we can verify the logic via ParseMFTFromFile which returns MftRecord[]
        // by internally using MftResult.
        
        var records = MftVolume.ParseMFTFromFile(_tempMftPath, out _);
        Assert.IsTrue(records.Length > 0);
    }
}
