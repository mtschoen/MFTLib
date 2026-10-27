using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib#144: the library owns both directions of its block file naming, so a consumer never
///     reimplements the inverse of <see cref="CacheDirectory.BlockFileName" />.
/// </summary>
[TestClass]
public sealed class CacheDirectoryEnumerationTests : IDisposable
{
    TempDirectory _directoryOwner = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directoryOwner = new TempDirectory("mftlib-cache");
        _cacheDirectory = _directoryOwner.Path;
    }

    [TestCleanup]
    public void Cleanup() => _directoryOwner.Dispose();

    public void Dispose() => Cleanup();

    string WriteFile(string fileName, string content)
    {
        var path = Path.Combine(_cacheDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [TestMethod]
    public void EnumerateCached_ReportsOnlyTheFilesThatMatchTheBlockFileNameFormat()
    {
        var first = WriteFile(CacheDirectory.BlockFileName(new VolumeKey('C'), 0x0BADF00D), "first block");
        var second = WriteFile(CacheDirectory.BlockFileName(new VolumeKey('D'), 0x12345678), "second");
        WriteFile("A-ZZZZZZZZ.mlix", "invalid hexadecimal serial");
        WriteFile("A_12345678.mlix", "invalid separator");
        WriteFile("notes.mlix", "an unrelated name that happens to end in the extension");
        WriteFile($"mftlib-cache-disabled-{Guid.NewGuid():N}-{CacheDirectory.BlockFileName(new VolumeKey('E'), 1)}", "disabled-cache temp file");
        WriteFile(CacheDirectory.BlockFileName(new VolumeKey('F'), 2) + ".retired-" + Guid.NewGuid().ToString("N"), "retired");

        var cached = CacheDirectory.EnumerateCached(_cacheDirectory)
            .OrderBy(entry => entry.Root).ToArray();

        Assert.HasCount(2, cached);
        Assert.AreEqual(new VolumeKey('C'), cached[0].Root);
        Assert.AreEqual(0x0BADF00Du, cached[0].VolumeSerial);
        Assert.AreEqual(first, cached[0].Path);
        Assert.AreEqual(new FileInfo(first).Length, cached[0].Size);
        Assert.AreEqual(File.GetLastWriteTimeUtc(first), cached[0].LastWriteTime);
        Assert.AreEqual(new VolumeKey('D'), cached[1].Root);
        Assert.AreEqual(0x12345678u, cached[1].VolumeSerial);
        Assert.AreEqual(second, cached[1].Path);
    }

    [TestMethod]
    public void EnumerateCached_IsTheInverseOfBlockFileNameAcrossTheLetterRangeAndEdgeSerials()
    {
        var expected = new List<(RootKey Root, uint VolumeSerial)>();
        var serials = new[] { 0u, 1u, 0x0BADF00Du, uint.MaxValue };
        for (var driveLetter = 'A'; driveLetter <= 'Z'; driveLetter++)
        {
            var serial = serials[(driveLetter - 'A') % serials.Length];
            WriteFile(CacheDirectory.BlockFileName(new VolumeKey(driveLetter), serial), "block");
            expected.Add((new VolumeKey(driveLetter), serial));
        }

        var cached = CacheDirectory.EnumerateCached(_cacheDirectory)
            .Select(entry => (entry.Root, entry.VolumeSerial)).ToArray();

        Assert.AreSequenceEqual(expected, cached, SequenceOrder.InAnyOrder);
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

            Assert.IsEmpty(CacheDirectory.EnumerateCached(_cacheDirectory), fileName);

            File.Delete(path);
        }
    }

    [TestMethod]
    public void EnumerateCached_ADigitOrSymbolInTheLetterPositionIsNotRecognised()
    {
        WriteFile("1-0BADF00D.mlix", "digit");
        WriteFile("_-0BADF00D.mlix", "symbol");

        Assert.IsEmpty(CacheDirectory.EnumerateCached(_cacheDirectory));
    }

    [TestMethod]
    public void EnumerateCached_AMissingDirectoryReturnsEmpty()
    {
        var absent = Path.Combine(_cacheDirectory, "absent");

        var cached = CacheDirectory.EnumerateCached(absent);

        Assert.IsEmpty(cached);
    }

    [TestMethod]
    public void EnumerateCached_AnEmptyDirectoryReturnsEmpty()
    {
        Assert.IsEmpty(CacheDirectory.EnumerateCached(_cacheDirectory));
    }
}
