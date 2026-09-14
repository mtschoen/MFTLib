using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib#144: the library owns both directions of its block file naming, so a consumer never
///     reimplements the inverse of <see cref="CacheDirectory.BlockFileName" />.
/// </summary>
[TestClass]
public class CacheDirectoryEnumerationTests
{
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cacheDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    string WriteFile(string fileName, string content)
    {
        var path = Path.Combine(_cacheDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [TestMethod]
    public void EnumerateCached_ReportsOnlyTheFilesThatMatchTheBlockFileNameFormat()
    {
        var first = WriteFile(CacheDirectory.BlockFileName('C', 0x0BADF00D), "first block");
        var second = WriteFile(CacheDirectory.BlockFileName('D', 0x12345678), "second");
        WriteFile("A-ZZZZZZZZ.mlix", "invalid hexadecimal serial");
        WriteFile("A_12345678.mlix", "invalid separator");
        WriteFile("notes.mlix", "an unrelated name that happens to end in the extension");
        WriteFile($"mftlib-nocache-{Guid.NewGuid():N}-{CacheDirectory.BlockFileName('E', 1)}", "no-cache temp file");
        WriteFile(CacheDirectory.BlockFileName('F', 2) + ".retired-" + Guid.NewGuid().ToString("N"), "retired");

        var cached = CacheDirectory.EnumerateCached(_cacheDirectory)
            .OrderBy(entry => entry.DriveLetter).ToArray();

        Assert.AreEqual(2, cached.Length);
        Assert.AreEqual('C', cached[0].DriveLetter);
        Assert.AreEqual(0x0BADF00Du, cached[0].VolumeSerial);
        Assert.AreEqual(first, cached[0].Path);
        Assert.AreEqual(new FileInfo(first).Length, cached[0].SizeBytes);
        Assert.AreEqual(File.GetLastWriteTimeUtc(first), cached[0].LastWriteTimeUtc);
        Assert.AreEqual('D', cached[1].DriveLetter);
        Assert.AreEqual(0x12345678u, cached[1].VolumeSerial);
        Assert.AreEqual(second, cached[1].Path);
    }

    [TestMethod]
    public void EnumerateCached_IsTheInverseOfBlockFileNameAcrossTheLetterRangeAndEdgeSerials()
    {
        var expected = new List<(char DriveLetter, uint VolumeSerial)>();
        var serials = new[] { 0u, 1u, 0x0BADF00Du, uint.MaxValue };
        for (var driveLetter = 'A'; driveLetter <= 'Z'; driveLetter++)
        {
            var serial = serials[(driveLetter - 'A') % serials.Length];
            WriteFile(CacheDirectory.BlockFileName(driveLetter, serial), "block");
            expected.Add((driveLetter, serial));
        }

        var cached = CacheDirectory.EnumerateCached(_cacheDirectory)
            .Select(entry => (entry.DriveLetter, entry.VolumeSerial)).ToArray();

        CollectionAssert.AreEquivalent(expected, cached);
    }

    [TestMethod]
    public void EnumerateCached_LowercaseFileNameIsNotRecognised()
    {
        // BlockFileName upper-cases the letter and formats the serial with X8, so a name that
        // does not round-trip through it is not one this library wrote. Windows preserves the
        // case a file was created with, so the parser is what decides on both platforms.
        foreach (var fileName in new[] { "c-0badf00d.mlix", "C-0badf00d.mlix", "c-0BADF00D.mlix" })
        {
            var path = WriteFile(fileName, "block");

            Assert.AreEqual(0, CacheDirectory.EnumerateCached(_cacheDirectory).Count, fileName);

            File.Delete(path);
        }
    }

    [TestMethod]
    public void EnumerateCached_ADigitOrSymbolInTheLetterPositionIsNotRecognised()
    {
        WriteFile("1-0BADF00D.mlix", "digit");
        WriteFile("_-0BADF00D.mlix", "symbol");

        Assert.AreEqual(0, CacheDirectory.EnumerateCached(_cacheDirectory).Count);
    }

    [TestMethod]
    public void EnumerateCached_AMissingDirectoryReturnsEmpty()
    {
        var absent = Path.Combine(_cacheDirectory, "absent");

        var cached = CacheDirectory.EnumerateCached(absent);

        Assert.AreEqual(0, cached.Count);
    }

    [TestMethod]
    public void EnumerateCached_AnEmptyDirectoryReturnsEmpty()
    {
        Assert.AreEqual(0, CacheDirectory.EnumerateCached(_cacheDirectory).Count);
    }
}
