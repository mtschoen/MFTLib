using System.Security.Cryptography;
using System.Text;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public sealed class CacheDirectoryNamingTests : IDisposable
{
    const uint Serial = 0x0BADF00D;
    TempDirectory _directoryOwner = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directoryOwner = new TempDirectory("mftlib-naming");
        _cacheDirectory = _directoryOwner.Path;
    }

    [TestCleanup]
    public void Cleanup()
    {
        DriveKind._directoryCaseSensitiveOverride = null;
        _directoryOwner.Dispose();
    }

    public void Dispose() => Cleanup();

    static RootKey Folder(string name) =>
        RootKey.FromDirectory(Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "Cache", name));

    static string ExpectedStem(string text) =>
        "D" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 8));

    string Seed(RootKey root, uint serial = Serial) =>
        SyntheticBlock.WriteCached(_cacheDirectory, root, serial,
            new SyntheticBlockOptions { ProducerKind = ProducerKind.Enumeration, RootRow = 0 },
            [new SyntheticRow(0, root.ToString(), 0) { IsDirectory = true }]);

    [TestMethod]
    public void BlockFileName_VolumeRoot_IsLetterDashUppercaseSerial()
    {
        Assert.AreEqual("C-0BADF00D.mlix", CacheDirectory.BlockFileName(new VolumeKey('c'), Serial));
        Assert.AreEqual("Z-00000000.mlix", CacheDirectory.BlockFileName(new VolumeKey('Z'), 0));
    }

    [TestMethod]
    public void BlockFileName_DirectoryRoot_IsHashOfTheKeyTextAndTheSerial()
    {
        DriveKind._directoryCaseSensitiveOverride = true;
        var key = Folder("Folder");

        var name = CacheDirectory.BlockFileName(key, Serial);

        Assert.AreEqual($"{ExpectedStem(key.ToString())}-0BADF00D.mlix", name);
        Assert.AreEqual(1 + 16 + 1 + 8 + 5, name.Length);
    }

    [TestMethod]
    public void BlockFileName_CaseInsensitivePlatform_IsCaseStable()
    {
        DriveKind._directoryCaseSensitiveOverride = false;
        var upper = Folder("Folder");
        var lower = Folder("folder");

        Assert.AreEqual(CacheDirectory.BlockFileName(lower, Serial), CacheDirectory.BlockFileName(upper, Serial));
        Assert.AreEqual($"{ExpectedStem(upper.ToString().ToUpperInvariant())}-0BADF00D.mlix",
            CacheDirectory.BlockFileName(upper, Serial));
    }

    [TestMethod]
    public void BlockFileName_CaseSensitivePlatform_DistinguishesCase()
    {
        DriveKind._directoryCaseSensitiveOverride = true;

        Assert.AreNotEqual(CacheDirectory.BlockFileName(Folder("Folder"), Serial),
            CacheDirectory.BlockFileName(Folder("folder"), Serial));
    }

    [TestMethod]
    public void BlockFileName_DifferentSerialsAndRoots_GiveDifferentNames()
    {
        var names = new[]
        {
            CacheDirectory.BlockFileName(Folder("A"), Serial),
            CacheDirectory.BlockFileName(Folder("B"), Serial),
            CacheDirectory.BlockFileName(Folder("A"), Serial + 1),
            CacheDirectory.BlockFileName(new VolumeKey('D'), Serial)
        };

        Assert.HasCount(names.Length, names.Distinct());
    }

    [TestMethod]
    public void BlockFileName_DefaultKey_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CacheDirectory.BlockFileName(default, Serial));
    }

    [TestMethod]
    public void TryParseBlockFileName_AcceptsBothShapes()
    {
        var directoryName = CacheDirectory.BlockFileName(Folder("Folder"), Serial);

        Assert.IsTrue(CacheDirectory.TryParseBlockFileName("C-0BADF00D.mlix", out var volume));
        Assert.AreEqual(new VolumeKey('C'), volume.Volume);
        Assert.AreEqual("C", volume.Stem);
        Assert.AreEqual(Serial, volume.VolumeSerial);

        Assert.IsTrue(CacheDirectory.TryParseBlockFileName(directoryName, out var directory));
        Assert.IsNull(directory.Volume);
        Assert.AreEqual(directoryName[..17], directory.Stem);
        Assert.AreEqual(Serial, directory.VolumeSerial);
    }

    [TestMethod]
    [DataRow("c-0BADF00D.mlix")]
    [DataRow("C-0badf00d.mlix")]
    [DataRow("1-0BADF00D.mlix")]
    [DataRow("C_0BADF00D.mlix")]
    [DataRow("C-0BADF00D.MLIX")]
    [DataRow("D0123456789ABCDEF-0BADF00D.mlix")]
    [DataRow("D0123456789abcde-0BADF00D.mlix")]
    [DataRow("D0123456789abcdeg-0BADF00D.mlix")]
    [DataRow("E0123456789abcdef-0BADF00D.mlix")]
    [DataRow("D0123456789abcdef-0badf00d.mlix")]
    [DataRow("D0123456789abcdef_0BADF00D.mlix")]
    [DataRow("")]
    public void TryParseBlockFileName_RejectsNamesTheFormatterWouldNotProduce(string fileName)
    {
        Assert.IsFalse(CacheDirectory.TryParseBlockFileName(fileName, out _));
    }

    [TestMethod]
    public void EnumerateCached_DirectoryRoot_ReadsTheStoredKey()
    {
        var key = Folder("Folder");
        var path = Seed(key);

        var cached = CacheDirectory.EnumerateCached(_cacheDirectory).Single();

        Assert.AreEqual(key, cached.Drive);
        Assert.AreEqual(Serial, cached.VolumeSerial);
        Assert.AreEqual(path, cached.Path);
    }

    [TestMethod]
    public void EnumerateCached_DirectoryNameThatDoesNotMatchTheStoredKey_IsRejected()
    {
        var path = Seed(Folder("Folder"));
        var misnamed = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName(Folder("Other"), Serial));
        File.Move(path, misnamed);
        var rejected = new List<CachedBlockRejection>();

        var cached = CacheDirectory.EnumerateCached(_cacheDirectory, rejected.Add);

        Assert.IsEmpty(cached);
        Assert.AreEqual(misnamed, rejected.Single().Path);
    }

    [TestMethod]
    public void EnumerateCached_DirectoryNameWithUnreadableKey_IsRejected()
    {
        var name = CacheDirectory.BlockFileName(Folder("Folder"), Serial);
        File.WriteAllBytes(Path.Combine(_cacheDirectory, name), new byte[BlockLayout.HeaderRegionBytes]);
        var rejected = new List<CachedBlockRejection>();

        Assert.IsEmpty(CacheDirectory.EnumerateCached(_cacheDirectory, rejected.Add));
        Assert.HasCount(1, rejected);
    }

    [TestMethod]
    public void InspectCached_DirectoryRoots_ReportTheStoredKeyAndFilterByKey()
    {
        var wanted = Folder("Wanted");
        Seed(wanted);
        Seed(Folder("Other"));
        Seed(new VolumeKey('T'));

        var all = CacheDirectory.InspectCached(_cacheDirectory);
        var selected = CacheDirectory.InspectCached(_cacheDirectory, [wanted]);

        Assert.HasCount(3, all);
        Assert.IsTrue(all.All(status => status.Availability == CachedBlockAvailability.Available));
        Assert.AreEqual(wanted, selected.Single().File.Drive);
        Assert.AreEqual(wanted.ToString(), selected.Single().RootDirectory);
    }

    [TestMethod]
    public void InspectCached_VolumeBlockRenamedToAnotherLetter_IsInvalidWithWrongRootDirectory()
    {
        var path = Seed(new VolumeKey('T'));
        File.Move(path, Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName(new VolumeKey('U'), Serial)));

        var status = CacheDirectory.InspectCached(_cacheDirectory).Single();

        Assert.AreEqual(CachedBlockAvailability.Invalid, status.Availability);
        Assert.AreEqual(BlockValidationResult.WrongRootDirectory, status.Validation);
    }

    [TestMethod]
    public void DeleteCached_DirectoryRoots_DeletesOnlyTheSelectedKeysBlocks()
    {
        var wanted = Folder("Wanted");
        var wantedPath = Seed(wanted);
        var otherPath = Seed(Folder("Other"));

        var results = CacheDirectory.DeleteCached(_cacheDirectory, [wanted]);

        Assert.AreEqual(CachedBlockDeletionOutcome.Deleted, results.Single().Outcome);
        Assert.AreEqual(wanted, results.Single().File.Drive);
        Assert.IsFalse(File.Exists(wantedPath));
        Assert.IsTrue(File.Exists(otherPath));
    }

    [TestMethod]
    public void DeleteCached_NullSelection_DeletesVolumeAndDirectoryBlocks()
    {
        var directoryPath = Seed(Folder("Folder"));
        var volumePath = Seed(new VolumeKey('T'));

        var results = CacheDirectory.DeleteCached(_cacheDirectory);

        Assert.HasCount(2, results);
        Assert.IsFalse(File.Exists(directoryPath));
        Assert.IsFalse(File.Exists(volumePath));
    }

    [TestMethod]
    public void DeleteCached_ExcludedDirectoryBlock_IsNeverOpened()
    {
        var excludedPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName(Folder("Excluded"), Serial));
        File.WriteAllBytes(excludedPath, new byte[BlockLayout.HeaderRegionBytes]);
        var rejected = new List<CachedBlockRejection>();

        var results = CacheDirectory.DeleteCached(_cacheDirectory, [Folder("Wanted")], rejected.Add);

        Assert.IsEmpty(results);
        Assert.IsEmpty(rejected);
        Assert.IsTrue(File.Exists(excludedPath));
    }

    [TestMethod]
    public void EqualNonAsciiKeys_ShareOneSlotAcrossNamingInspectionAndDeletion()
    {
        DriveKind._directoryCaseSensitiveOverride = false;
        var medial = Folder("\u03C3");
        var final = Folder("\u03C2");
        Assert.AreEqual(medial, final);

        Assert.AreEqual(CacheDirectory.BlockFileName(medial, Serial), CacheDirectory.BlockFileName(final, Serial));
        var path = Seed(medial);
        Assert.AreEqual(path, SyntheticBlock.CachedPath(_cacheDirectory, final, Serial));
        Assert.AreEqual(path, CacheDirectory.InspectCached(_cacheDirectory, [final]).Single().File.Path);

        var deleted = CacheDirectory.DeleteCached(_cacheDirectory, [final]);

        Assert.AreEqual(CachedBlockDeletionOutcome.Deleted, deleted.Single().Outcome);
        Assert.IsFalse(File.Exists(path));
    }
}
