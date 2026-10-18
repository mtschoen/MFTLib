using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public sealed class IndexedDriveTests : IDisposable
{
    IndexTreeFixture _fixture = null!;
    string _treeRoot => _fixture.TreeRoot;
    string _cacheDirectory => _fixture.CacheDirectory;

    [TestInitialize]
    public void Initialize() => _fixture = new IndexTreeFixture(seedDocuments: false);

    public void Dispose()
    {
        IndexedDrive.Dependencies.ResetToDefaults();
        _fixture.Dispose();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    [DataRow('C')]
    [DataRow('c')]
    public void FromWindowsVolume_NormalizesKeysToTheSameDrive(char spelling)
    {
        IndexedDrive.Dependencies.VolumeSerialReader = root =>
        {
            Assert.AreEqual(@"C:\", root);
            return 0xDEADBEEF;
        };

        var drive = IndexedDrive.FromWindowsVolume(spelling);

        Assert.AreEqual('C', drive.Drive);
        Assert.AreEqual(@"C:\", drive.RootDirectory);
        Assert.AreEqual(0xDEADBEEFu, drive.VolumeSerial);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void FromWindowsVolume_ThrowsIOExceptionNamingTheDriveWhenTheQueryFails()
    {
        IndexedDrive.Dependencies.VolumeSerialReader = _ => null;

        var failure = Assert.ThrowsExactly<IOException>(() => IndexedDrive.FromWindowsVolume('Q'));

        StringAssert.Contains(failure.Message, "Q");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void FromWindowsVolume_RejectsDefaultKey()
    {
        Assert.ThrowsExactly<ArgumentException>(() => IndexedDrive.FromWindowsVolume(default));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void FromWindowsVolume_ReadsARealSerialForTheSystemDrive()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows-only: GetVolumeInformationW against a real drive");
        }

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        var drive = IndexedDrive.FromWindowsVolume(systemRoot[0]);

        Assert.AreNotEqual(0u, drive.VolumeSerial);
        Assert.AreEqual(systemRoot, drive.RootDirectory);
    }

    [TestMethod]
    [DataRow(DriveType.Fixed, true, "NTFS", null)]
    [DataRow(DriveType.Fixed, true, "ntfs", null)]
    [DataRow(DriveType.Fixed, true, "exFAT", "not NTFS (exFAT)")]
    [DataRow(DriveType.Fixed, false, "NTFS", "not ready")]
    [DataRow(DriveType.Network, true, "NTFS", "network")]
    [DataRow(DriveType.Removable, false, "NTFS", "removable")]
    [DataRow(DriveType.CDRom, false, "CDFS", "optical")]
    [DataRow(DriveType.NoRootDirectory, false, "", "not ready")]
    [DataRow(DriveType.Ram, true, "NTFS", "other drive type")]
    public void ReadVolumeVerdicts_ClassifiesByTypeThenReadinessThenFormat(
        DriveType type, bool ready, string format, string? reason)
    {
        var result = IndexedDrive.ReadVolumeVerdicts(new[] { 0 }, _ => @"X:\", _ => type,
            _ => type == DriveType.Fixed ? ready
                : throw new AssertFailedException("Non-fixed readiness was read."),
            _ => type == DriveType.Fixed && ready ? format
                : throw new AssertFailedException("Format was read without a ready fixed drive."));

        var volume = result.Single();
        Assert.AreEqual(new DriveKey('X'), volume.Drive);
        Assert.AreEqual(@"X:\", volume.RootDirectory);
        Assert.AreEqual(type, volume.DriveType);
        Assert.AreEqual(type == DriveType.Fixed && ready, volume.IsReady);
        Assert.AreEqual(type == DriveType.Fixed && ready ? format : null, volume.Format);
        Assert.AreEqual(reason is null, volume.IsIndexable);
        Assert.AreEqual(reason, volume.Reason);
    }

    [TestMethod]
    public void ReadVolumeVerdicts_ReportsVanishedDriveNotReadyAndContinues()
    {
        var result = IndexedDrive.ReadVolumeVerdicts(new[] { 0, 1 },
            drive => drive == 0 ? @"Q:\" : @"C:\", _ => DriveType.Fixed,
            drive => drive == 0 ? throw new IOException("device vanished") : true, _ => "NTFS");

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(new DriveKey('Q'), result[0].Drive);
        Assert.IsFalse(result[0].IsReady);
        Assert.IsNull(result[0].Format);
        Assert.AreEqual("not ready", result[0].Reason);
        Assert.IsTrue(result[1].IsIndexable);
        Assert.IsTrue(result[1].IsReady);
        Assert.AreEqual("NTFS", result[1].Format);
    }

    [TestMethod]
    public void ConstructorAndWithInitializer_RejectNullRootDirectory()
    {
        var valid = new IndexedDrive('T', @"T:\", 1);
        Assert.ThrowsExactly<ArgumentNullException>(() => new IndexedDrive('T', null!, 1));
        Assert.ThrowsExactly<ArgumentNullException>(() => valid with { RootDirectory = null! });
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t\r\n")]
    public void ConstructorAndWithInitializer_RejectEmptyOrWhiteSpaceRootDirectory(string blankRoot)
    {
        var valid = new IndexedDrive('T', @"T:\", 1);
        Assert.ThrowsExactly<ArgumentException>(() => new IndexedDrive('T', blankRoot, 1));
        Assert.ThrowsExactly<ArgumentException>(() => valid with { RootDirectory = blankRoot });
    }

    [TestMethod]
    [DataRow((ProducerKind)uint.MaxValue)]
    [DataRow(default(ProducerKind))]
    [DataRow((ProducerKind)999)]
    public void ConstructorAndWithInitializer_RejectUndefinedProducerKind(ProducerKind invalidPolicy)
    {
        var valid = new IndexedDrive('T', @"T:\", 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new IndexedDrive('T', @"T:\", 1, invalidPolicy));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => valid with { Producer = invalidPolicy });
    }

    [TestMethod]
    public void ConstructorAndWithInitializer_AcceptValidValues()
    {
        var drive = new IndexedDrive('t', @"T:\root", 42, ProducerKind.Enumeration);
        Assert.AreEqual(new DriveKey('T'), drive.Drive);
        Assert.AreEqual(@"T:\root", drive.RootDirectory);
        Assert.AreEqual(42u, drive.VolumeSerial);
        Assert.AreEqual(ProducerKind.Enumeration, drive.Producer);

        var updated = drive with
        {
            Drive = 'u',
            RootDirectory = @"U:\other",
            VolumeSerial = 99,
            Producer = ProducerKind.Mft
        };
        Assert.AreEqual(new DriveKey('U'), updated.Drive);
        Assert.AreEqual(@"U:\other", updated.RootDirectory);
        Assert.AreEqual(99u, updated.VolumeSerial);
        Assert.AreEqual(ProducerKind.Mft, updated.Producer);
    }

    [TestMethod]
    [DataRow('T', 'T')]
    [DataRow('T', 't')]
    [DataRow('t', 'T')]
    [DataRow('t', 't')]
    public async Task OpenAsync_RejectsDuplicateDriveKeysBeforeSideEffects(char firstLetter, char secondLetter)
    {
        Directory.CreateDirectory(_treeRoot);
        var absentRoot = Path.Combine(_treeRoot, "absent");
        var first = new IndexedDrive(firstLetter, _treeRoot, 1, ProducerKind.Enumeration);
        var second = new IndexedDrive(secondLetter, absentRoot, 2, ProducerKind.Enumeration);

        var failure = await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(
            options => options with { Drives = [first, second] });

        Assert.AreEqual("options", failure.ParamName);
        StringAssert.Contains(failure.Message, "Drive T is listed more than once.");
    }

    [TestMethod]
    public async Task OpenAsync_RejectsNullDriveListBeforeSideEffects()
    {
        var failure = await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(
            options => options with { Drives = null! });

        Assert.AreEqual("options", failure.ParamName);
        StringAssert.Contains(failure.Message, "FileIndexOptions.Drives");
    }

    [TestMethod]
    public async Task OpenAsync_RejectsNullDriveElementBeforeSideEffects()
    {
        Directory.CreateDirectory(_treeRoot);
        var valid = new IndexedDrive('T', _treeRoot, 1, ProducerKind.Enumeration);

        var singleNull = await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(
            options => options with { Drives = [null!] });
        Assert.AreEqual("options", singleNull.ParamName);

        var secondNull = await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(
            options => options with { Drives = [valid, null!] });
        Assert.AreEqual("options", secondNull.ParamName);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public async Task OpenAsync_RejectsBlankRootDirectoryBeforeSideEffects(string blankRoot)
    {
        await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(
            options => options with { Drives = [new IndexedDrive('T', blankRoot, 1, ProducerKind.Enumeration)] });

        var bypassed = CreateBypassedDrive(new DriveKey('T'), blankRoot, ProducerKind.Enumeration);
        await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(
            options => options with { Drives = [bypassed] });
    }

    [TestMethod]
    [DataRow((ProducerKind)uint.MaxValue)]
    [DataRow(default(ProducerKind))]
    [DataRow((ProducerKind)999)]
    public async Task OpenAsync_RejectsUndefinedProducerKindBeforeSideEffects(ProducerKind invalidPolicy)
    {
        Directory.CreateDirectory(_treeRoot);
        await AssertInvalidOpenCreatesNothingAsync<ArgumentOutOfRangeException>(
            options => options with { Drives = [new IndexedDrive('T', _treeRoot, 1, invalidPolicy)] });

        var bypassed = CreateBypassedDrive(new DriveKey('T'), _treeRoot, invalidPolicy);
        await AssertInvalidOpenCreatesNothingAsync<ArgumentOutOfRangeException>(
            options => options with { Drives = [bypassed] });
    }

    [TestMethod]
    public async Task OpenAsync_RejectsDefaultDriveKeyBeforeSideEffects()
    {
        Directory.CreateDirectory(_treeRoot);
        var bypassed = CreateBypassedDrive(default, _treeRoot, ProducerKind.Enumeration);

        await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(
            options => options with { Drives = [bypassed] });
    }

    [TestMethod]
    public async Task OpenAsync_AcceptsDistinctValidKeysAndMaterializesOnce()
    {
        var rootT = Path.Combine(_treeRoot, "T");
        var rootU = Path.Combine(_treeRoot, "U");
        Directory.CreateDirectory(rootT);
        Directory.CreateDirectory(rootU);
        await File.WriteAllTextAsync(Path.Combine(rootT, "t.txt"), "t");
        await File.WriteAllTextAsync(Path.Combine(rootU, "u.txt"), "u");

        var sequence = new SinglePassDriveList(
        [
            new IndexedDrive('T', rootT, 1, ProducerKind.Enumeration),
            new IndexedDrive('u', rootU, 2, ProducerKind.Enumeration)
        ]);

        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = sequence,
            CacheMode = CacheMode.Disabled
        }, CancellationToken.None);

        Assert.AreEqual(1, sequence.EnumerationCount);
        CollectionAssert.AreEqual(new DriveKey[] { 'T', 'U' },
            index.Drives.Select(status => status.Drive).ToArray());
        Assert.IsTrue(index.Drives.All(status => status.State == DriveState.Ready));

        var rescanResults = await index.RescanAsync(
            index.Drives.Select(status => status.Drive), CancellationToken.None);
        CollectionAssert.AreEqual(new DriveKey[] { 'T', 'U' },
            rescanResults.Select(result => result.Drive).ToArray());
        Assert.IsTrue(rescanResults.All(result => result.Outcome == DriveOperationOutcome.Succeeded));
    }

    [TestMethod]
    public async Task OpenAsync_InvalidConfigurationLeavesAnExistingOwnedCacheDirectoryUntouched()
    {
        Directory.CreateDirectory(_cacheDirectory);
        var existingFile = Path.Combine(_cacheDirectory, "existing.txt");
        await File.WriteAllTextAsync(existingFile, "keep");
        var contentsBefore = SnapshotDirectory(_cacheDirectory);
        var protectionBefore = ReadProtection(_cacheDirectory);
        var producerCalls = 0;
        var progressCalls = 0;
        Directory.CreateDirectory(_treeRoot);
        var first = new IndexedDrive('T', _treeRoot, 1, ProducerKind.Enumeration);
        var duplicate = new IndexedDrive('t', _treeRoot, 2, ProducerKind.Enumeration);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => FileIndex.OpenAsync(new FileIndexOptions
        {
            CacheMode = CacheMode.Persistent,
            CacheDirectory = _cacheDirectory,
            Drives = [first, duplicate],
            MftSource = new MftIndexSource((_, _) =>
            {
                Interlocked.Increment(ref producerCalls);
                return Task.FromException<MftBlockProduceResult>(new InvalidOperationException("producer must not run"));
            }),
            ScanProgress = new SynchronousProgress<IndexScanProgress>(_ => Interlocked.Increment(ref progressCalls)),
            DriveOpenProgress = new SynchronousProgress<IndexDriveOpened>(_ => Interlocked.Increment(ref progressCalls))
        }, CancellationToken.None));

        Assert.AreEqual(0, producerCalls);
        Assert.AreEqual(0, progressCalls);
        CollectionAssert.AreEqual(contentsBefore, SnapshotDirectory(_cacheDirectory),
            "no block slot, lock file or other entry may appear or vanish");
        Assert.AreEqual("keep", await File.ReadAllTextAsync(existingFile));
        Assert.AreEqual(protectionBefore, ReadProtection(_cacheDirectory));
    }

    static string ReadProtection(string directory) =>
        OperatingSystem.IsWindows()
            ? new DirectoryInfo(directory).GetAccessControl()
                .GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access)
            : File.GetUnixFileMode(directory).ToString();

    static string[] SnapshotDirectory(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(entry => $"{Path.GetRelativePath(directory, entry)}|{(File.Exists(entry) ? new FileInfo(entry).Length : -1)}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    async Task<TException> AssertInvalidOpenCreatesNothingAsync<TException>(
        Func<FileIndexOptions, FileIndexOptions> configure)
        where TException : Exception
    {
        var producerCalls = 0;
        var scanProgressCalls = 0;
        var openProgressCalls = 0;
        var baseOptions = new FileIndexOptions
        {
            CacheMode = CacheMode.Persistent,
            CacheDirectory = _cacheDirectory,
            MftSource = new MftIndexSource((_, _) =>
            {
                Interlocked.Increment(ref producerCalls);
                return Task.FromException<MftBlockProduceResult>(
                    new InvalidOperationException("producer must not run"));
            }),
            ScanProgress = new SynchronousProgress<IndexScanProgress>(_ => Interlocked.Increment(ref scanProgressCalls)),
            DriveOpenProgress = new SynchronousProgress<IndexDriveOpened>(_ => Interlocked.Increment(ref openProgressCalls))
        };

        var exception = await Assert.ThrowsExactlyAsync<TException>(
            () => FileIndex.OpenAsync(configure(baseOptions), CancellationToken.None));

        Assert.AreEqual(0, producerCalls);
        Assert.AreEqual(0, scanProgressCalls);
        Assert.AreEqual(0, openProgressCalls);
        Assert.IsFalse(Directory.Exists(_cacheDirectory));
        return exception;
    }

    static IndexedDrive CreateBypassedDrive(DriveKey drive, string? rootDirectory, ProducerKind policy)
    {
        var instance = (IndexedDrive)RuntimeHelpers.GetUninitializedObject(typeof(IndexedDrive));
        foreach (var field in typeof(IndexedDrive).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (field.FieldType == typeof(DriveKey))
            {
                field.SetValue(instance, drive);
            }
            else if (field.FieldType == typeof(string))
            {
                field.SetValue(instance, rootDirectory);
            }
            else if (field.FieldType == typeof(ProducerKind))
            {
                field.SetValue(instance, policy);
            }
        }

        return instance;
    }

    sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    sealed class SinglePassDriveList(IReadOnlyList<IndexedDrive> items) : IReadOnlyList<IndexedDrive>
    {
        public int EnumerationCount { get; private set; }

        public int Count => items.Count;

        public IndexedDrive this[int index] => items[index];

        public IEnumerator<IndexedDrive> GetEnumerator()
        {
            Assert.AreEqual(0, EnumerationCount, "FileIndexOptions.Drives must be materialized once.");
            EnumerationCount++;
            return items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
