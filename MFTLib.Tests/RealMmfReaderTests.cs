using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class RealMmfReaderTests
{
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Read_ValidMmf_ReturnsAllRecordsAsArray()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named memory-mapped files require Windows.");
        }

        var records = new[]
        {
            new ScanRecord(1, 0, 512, 1000, 0x20, false, "a.txt", "C:\\a.txt"),
            new ScanRecord(2, 0, 1024, 2000, 0x20, false, "b.txt", "C:\\b.txt"),
            new ScanRecord(3, 0, 2048, 3000, 0x10, true, "dir", "C:\\dir")
        };

        var mapName = "mftlib-real-mmf-reader-test-" + Guid.NewGuid().ToString("N");
        var byteLength = ScanPayload.ComputeSize(records);
        using var map = MemoryMappedFile.CreateNew(mapName, byteLength);
        using (var view = map.CreateViewStream(0, byteLength, MemoryMappedFileAccess.Write))
        {
            var buffer = new byte[byteLength];
            ScanPayload.Write(buffer, records);
            view.Write(buffer, 0, buffer.Length);
        }

        var reader = new RealMmfReader();
        var result = reader.Read(mapName, byteLength);

        CollectionAssert.AreEqual(records, result);
    }
}
