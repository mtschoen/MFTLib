using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexQueryTests
{
    string _treeRoot = null!;
    string _cacheDirectory = null!;
    FileIndex _index = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents", "Projects"));
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Pictures"));
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "Projects", "report.pdf"), new string('x', 100));
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Pictures", "readme.md"), "world");
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Pictures", "holiday.jpg"), new string('y', 5000));

        _index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory
        }, CancellationToken.None);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _index.DisposeAsync();
        foreach (var directory in new[] { _treeRoot, _cacheDirectory })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows.
            }
        }
    }

    [TestMethod]
    public void Find_ResolvesAPathToItsEntry()
    {
        var entry = _index.Find(@"T:\Documents\Projects\report.pdf");
        Assert.IsTrue(entry.HasValue);
        Assert.AreEqual(100L, entry.Value.Size);
    }

    [TestMethod]
    public void FindByName_SpansTheWholeDrive()
    {
        var results = _index.FindByName("readme.md");
        Assert.AreEqual(2, results.Count);
    }

    [TestMethod]
    public void Search_ReturnsTheWholeMatchSetForSlicing()
    {
        var results = _index.Search(new SearchQuery("read"));
        Assert.AreEqual(2, results.Count);

        var firstPage = results.Take(1).ToArray();
        Assert.AreEqual(1, firstPage.Length);
    }

    [TestMethod]
    public void Search_UnderRestrictsToASubtree()
    {
        var pictures = _index.Find(@"T:\Pictures")!.Value;
        var results = _index.Search(new SearchQuery("readme", Under: pictures));
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(@"T:\Pictures\readme.md", results[0].Path);
    }

    [TestMethod]
    public void Largest_ReturnsTheBiggestFiles()
    {
        var results = _index.Largest(2);
        Assert.AreEqual("holiday.jpg", results[0].Name);
        Assert.AreEqual("report.pdf", results[1].Name);
    }

    [TestMethod]
    public void DuplicateNames_FindsTheRepeatedName()
    {
        var groups = _index.DuplicateNames();
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual("readme.md", groups[0].Name);
    }

    [TestMethod]
    public void Root_ReturnsTheDriveRoot()
    {
        Assert.AreEqual(@"T:\", _index.Root('T').Path);
    }

    [TestMethod]
    public void Children_OfTheRootAreTheTopLevelDirectories()
    {
        var names = _index.Root('T').Children().Select(child => child.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "Documents", "Pictures" }, names);
    }

    [TestMethod]
    public void Open_ReadsTheRealFileBehindAnEnumerationEntry()
    {
        var entry = _index.Find(@"T:\Documents\readme.md")!.Value;
        using var stream = entry.Open(FileAccess.Read);
        using var reader = new StreamReader(stream);
        Assert.AreEqual("hello", reader.ReadToEnd());
    }

    [TestMethod]
    public void Search_AfterARescanSeesNewFiles()
    {
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "added.md"), "added");
        _index.RescanAsync('T', CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual(1, _index.Search(new SearchQuery("added")).Count);
    }

    [TestMethod]
    public void Scan_ReturnsAWorkingScannerOverTheDrive()
    {
        Assert.IsTrue(_index.TryGetDriveOrdinal('T', out var driveOrdinal));

        var rowCount = 0;
        foreach (var _ in _index.Scan(driveOrdinal))
        {
            rowCount++;
        }

        Assert.IsTrue(rowCount > 0);
    }

    [TestMethod]
    public void HandleFromAnOldSnapshot_StaysReadableAcrossARescan()
    {
        var before = _index.Find(@"T:\Documents\readme.md")!.Value;
        _index.RescanAsync('T', CancellationToken.None).GetAwaiter().GetResult();

        // The old block stays mapped because this handle still references its snapshot.
        Assert.AreEqual("readme.md", before.Name);
        Assert.AreEqual(@"T:\Documents\readme.md", before.Path);
    }
}
