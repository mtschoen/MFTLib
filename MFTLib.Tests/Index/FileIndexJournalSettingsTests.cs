using System.Runtime.Versioning;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.QueryUsnJournalSettings" />: drive membership validation and
///     delegation to the query engine. The engine itself is swapped out through
///     <c>UsnJournalSettingsQuery.OverrideQueryForTest</c>, so these run on every platform
///     (the <see cref="SupportedOSPlatformAttribute" /> on each test satisfies CA1416 and
///     is inert at runtime, same as <c>IndexedDriveTests</c>).
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexJournalSettingsTests
{
    string _treeRoot = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_treeRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in new[] { _treeRoot, _cacheDirectory })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A just-unmapped block file can stay locked briefly on Windows.
            }
        }
    }

    Task<FileIndex> OpenIndexAsync()
    {
        return FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        }, CancellationToken.None);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task QueryUsnJournalSettings_ConfiguredDrive_ReturnsEngineResult()
    {
        var expected = new UsnJournalSettings
        {
            MaximumSize = 64L * 1024 * 1024,
            AllocationDelta = 8L * 1024 * 1024
        };
        using var restore = UsnJournalSettingsQuery.OverrideQueryForTest(
            drive => drive == 'T'
                ? expected
                : throw new IOException($"Unexpected drive {drive}"));
        await using var index = await OpenIndexAsync();

        // Lowercase input normalizes to the configured uppercase letter.
        var settings = index.QueryUsnJournalSettings('t');

        Assert.AreEqual(expected.MaximumSize, settings.MaximumSize);
        Assert.AreEqual(expected.AllocationDelta, settings.AllocationDelta);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task QueryUsnJournalSettings_UnconfiguredDrive_ThrowsArgumentException()
    {
        await using var index = await OpenIndexAsync();

        try
        {
            index.QueryUsnJournalSettings('Q');
            Assert.Fail("Expected ArgumentException");
        }
        catch (ArgumentException exception)
        {
            StringAssert.Contains(exception.Message, "Q");
        }
    }
}
