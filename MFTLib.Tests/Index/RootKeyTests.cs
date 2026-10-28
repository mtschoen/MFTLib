using System.Reflection;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public class RootKeyTests
{
    [TestCleanup]
    public void Cleanup() => RootKind._directoryCaseSensitiveOverride = null;

    [TestMethod]
    public void VolumeCase_WidensAndNarrowsWithoutConversion()
    {
        RootKey key = new VolumeKey('c');

        Assert.IsTrue(key.IsVolume);
        Assert.IsTrue(key.TryGetVolume(out var volume));
        Assert.AreEqual(new VolumeKey('C'), volume);
        Assert.AreEqual("C", key.ToString());
    }

    [TestMethod]
    public void DefaultKey_IsInvalid()
    {
        var key = default(RootKey);

        Assert.IsFalse(key.TryGetVolume(out _));
        Assert.ThrowsExactly<ArgumentException>(() => key.ToString());
        Assert.ThrowsExactly<ArgumentException>(() => key.CompareTo(new VolumeKey('C')));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void FromDirectory_VolumeRoot_SelectsVolumeCase()
    {
        var key = RootKey.FromDirectory("c:/");

        Assert.IsTrue(key.IsVolume);
        Assert.IsTrue(key.TryGetVolume(out var volume));
        Assert.AreEqual(new VolumeKey('C'), volume);
        Assert.AreEqual("C", key.ToString());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void FromDirectory_Directory_NormalizesTrailingAndMixedSeparators()
    {
        var key = RootKey.FromDirectory("C:\\parent/child\\");

        Assert.IsFalse(key.IsVolume);
        Assert.IsFalse(key.TryGetVolume(out _));
        Assert.AreEqual("C:\\parent\\child", key.ToString());
    }

    [TestMethod]
    [DataRow("relative")]
    [DataRow(".\\relative")]
    [DataRow("C:folder")]
    public void FromDirectory_RejectsRelativePaths(string path) =>
        Assert.ThrowsExactly<ArgumentException>(() => RootKey.FromDirectory(path));

    [TestMethod]
    public void FromDirectory_RejectsUnpairedSurrogates()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var path = Path.Combine(root, "folder", "\uD800");

        Assert.ThrowsExactly<ArgumentException>(() => RootKey.FromDirectory(path));
    }

    [TestMethod]
    [DataRow("C")]
    [DataRow("c:")]
    [DataRow("C:\\")]
    [DataRow("c:/")]
    public void TryParse_DriveSpec_SelectsVolumeCase(string text)
    {
        Assert.IsTrue(RootKey.TryParse(text, out var key));
        Assert.IsTrue(key.TryGetVolume(out var volume));
        Assert.AreEqual(new VolumeKey('C'), volume);
    }

    [TestMethod]
    public void TryParse_Directory_NormalizesPath()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var path = Path.Combine(root, "parent", "child") + Path.DirectorySeparatorChar;

        Assert.IsTrue(RootKey.TryParse(path, out var key));

        Assert.IsFalse(key.IsVolume);
        Assert.AreEqual(Path.Combine(root, "parent", "child"), key.ToString());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t")]
    [DataRow("relative")]
    public void TryParse_InvalidDirectory_ReturnsFalseAndDefault(string? text)
    {
        Assert.IsFalse(RootKey.TryParse(text, out var key));
        Assert.AreEqual(default(RootKey), key);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DirectoryEqualityAndHashCode_UsePlatformCasePolicy(bool caseSensitive)
    {
        RootKind._directoryCaseSensitiveOverride = caseSensitive;
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var first = RootKey.FromDirectory(Path.Combine(root, "Parent", "Folder"));
        var second = RootKey.FromDirectory(Path.Combine(root, "Parent", "folder"));

        Assert.AreEqual(!caseSensitive, first.Equals(second));
        Assert.AreEqual(!caseSensitive, first.GetHashCode() == second.GetHashCode());
    }

    [TestMethod]
    public void CompareTo_OrdersVolumesByLetterBeforeDirectoriesByOrdinalText()
    {
        RootKind._directoryCaseSensitiveOverride = false;
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        RootKey volumeA = new VolumeKey('A');
        RootKey volumeC = new VolumeKey('C');
        var upperDirectory = RootKey.FromDirectory(Path.Combine(root, "Alpha"));
        var lowerDirectory = RootKey.FromDirectory(Path.Combine(root, "alpha"));

        Assert.IsLessThan(0, volumeA.CompareTo(volumeC));
        Assert.IsLessThan(0, volumeC.CompareTo(upperDirectory));
        Assert.IsLessThan(0, upperDirectory.CompareTo(lowerDirectory));
        Assert.IsGreaterThan(0, lowerDirectory.CompareTo(upperDirectory));
    }

    [TestMethod]
    public void FromDirectory_AcceptsMaximumStoredKeyLength()
    {
        var path = FullPathWithLength(1983);

        Assert.AreEqual(path, RootKey.FromDirectory(path).ToString());
    }

    [TestMethod]
    public void FromDirectory_RejectsPathBeyondStoredKeyLength()
    {
        var path = FullPathWithLength(1984);

        Assert.ThrowsExactly<ArgumentException>(() => RootKey.FromDirectory(path));
    }

    [TestMethod]
    public void NotificationRecords_RejectDefaultKeysOnConstructionAndWithInitialization()
    {
        var settings = new UsnJournalSettings { MaximumSize = 65536, AllocationDelta = 4096 };
        var cachedBlockFile = new CachedBlockFile(new VolumeKey('C'), 1, "unused", 0, DateTime.UnixEpoch);
        var rootStatus = new RootStatus(new VolumeKey('C'), RootState.Offline);
        var scanProgress = new IndexScanProgress(new VolumeKey('C'), IndexScanPhase.Finished, 0);
        var checkpointLoss = new JournalCheckpointLoss(new VolumeKey('C'), JournalCheckpointLossDetection.LiveWatch,
            JournalCheckpointLossCause.CheckpointTrimmed, settings);
        var watchFault = new WatchFault(WatchFaultKind.Channel, new VolumeKey('C'), new InvalidOperationException());
        var windowsVolume = SyntheticNotifications.CreateWindowsVolume(new VolumeKey('C'), "C:\\", DriveType.Fixed);
        Assert.ThrowsExactly<ArgumentException>(() => cachedBlockFile with { Root = default });
        Assert.ThrowsExactly<ArgumentException>(() => rootStatus with { Root = default });
        Assert.ThrowsExactly<ArgumentException>(() => scanProgress with { Root = default });
        Assert.ThrowsExactly<ArgumentException>(() => checkpointLoss with { Volume = default });
        Assert.ThrowsExactly<ArgumentException>(() => watchFault with { Volume = default });
        Assert.ThrowsExactly<ArgumentException>(() => windowsVolume with { Volume = default });
        Assert.ThrowsExactly<ArgumentException>(() =>
            SyntheticNotifications.CreateWindowsVolume(default, "C:\\", DriveType.Fixed));
    }

    [TestMethod]
    public void PublicRecords_RejectDefaultKeysOnConstructionAndWithInitialization()
    {
        Assert.ThrowsExactly<ArgumentException>(() => SyntheticIndexedRoot.Create(default, "unused", 0));
        Assert.ThrowsExactly<ArgumentException>(() => new IndexRecordKey(default, 5, ProducerKind.Mft));
        Assert.ThrowsExactly<ArgumentException>(() => new RootOperationResult(default, RootOperationOutcome.Succeeded, null));
        Assert.ThrowsExactly<ArgumentException>(() => new WatchStateChange(default, new RootWatchStatus(), null));
        var root = SyntheticIndexedRoot.Create(new VolumeKey('c'), "unused", 0);
        var recordKey = new IndexRecordKey(new VolumeKey('C'), 5, ProducerKind.Mft);
        var operationResult = new RootOperationResult(new VolumeKey('C'), RootOperationOutcome.Succeeded, null);
        var stateChange = new WatchStateChange(new VolumeKey('C'), new RootWatchStatus(), null);
        Assert.ThrowsExactly<ArgumentException>(() => root with { Root = default });
        Assert.ThrowsExactly<ArgumentException>(() => recordKey with { Root = default });
        Assert.ThrowsExactly<ArgumentException>(() => operationResult with { Root = default });
        Assert.ThrowsExactly<ArgumentException>(() => stateChange with { Volume = default });
        Assert.ThrowsExactly<ArgumentException>(() =>
            SyntheticNotifications.CreateWatchStateChange(default, new RootWatchStatus(), null));
        Assert.ThrowsExactly<ArgumentException>(() =>
            SyntheticNotifications.CreateRootOperationResult(default, RootOperationOutcome.Succeeded, null));
    }

    [TestMethod]
    public void CacheSelections_RejectDefaultEvenWhenDirectoryIsMissing()
    {
        using var directoryOwner = new TempDirectory("mftlib-missing-root-key", create: false);
        var directory = directoryOwner.Path;
        Assert.ThrowsExactly<ArgumentException>(() => CacheDirectory.InspectCached(directory, [default]));
        Assert.ThrowsExactly<ArgumentException>(() => CacheDirectory.DeleteCached(directory, [default]));
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public void DumpFactory_RejectsDefaultKey() =>
        Assert.ThrowsExactly<ArgumentException>(() => MftIndexSources.FromMftDumpFile("unused.mft", default));

    [TestMethod]
    public void TestPackageRecords_NormalizeAndRejectDefaultConstructionAndInitialization()
    {
        var constructor = typeof(InProcessBrokerScan).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters() is [{ } parameter] && parameter.ParameterType == typeof(VolumeKey));
        Assert.IsTrue(constructor.IsAssembly, "Scan observations are constructed inside the harness.");
        var scan = new InProcessBrokerScan('c');
        var start = new ScriptedWatchStart('c', default);
        Assert.AreEqual('C', scan.Volume);
        Assert.AreEqual('C', start.Volume);
        Assert.AreEqual('D', (scan with { Volume = 'd' }).Volume);
        Assert.AreEqual('D', (start with { Volume = 'd' }).Volume);
        Assert.ThrowsExactly<ArgumentException>(() => new InProcessBrokerScan(default));
        Assert.ThrowsExactly<ArgumentException>(() => new ScriptedWatchStart(default, default));
        Assert.ThrowsExactly<ArgumentException>(() => scan with { Volume = default });
        Assert.ThrowsExactly<ArgumentException>(() => start with { Volume = default });
    }

    static string FullPathWithLength(int length)
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var prefix = Path.Combine(root, "a") + Path.DirectorySeparatorChar;
        return prefix + new string('x', length - prefix.Length);
    }
}
