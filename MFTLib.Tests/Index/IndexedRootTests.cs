using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public sealed class IndexedRootTests : IndexTreeTestBase
{
    protected override bool SeedDocuments => false;

    public override void Dispose()
    {
        try
        {
            IndexedRoot.Dependencies.ResetToDefaults();
        }
        finally
        {
            base.Dispose();
        }
    }

    [TestMethod]
    [DataRow('C')]
    [DataRow('c')]
    public void ReadWindowsVolume_NormalizesKeysToTheSameRoot(char spelling)
    {
        IndexedRoot.Dependencies._volumeSerialReader = root =>
        {
            Assert.AreEqual(@"C:\", root);
            return 0xDEADBEEF;
        };

        var root = IndexedRoot.ReadWindowsVolume(spelling);

        Assert.AreEqual("C", root.Root.ToString());
        Assert.AreEqual(@"C:\", root.RootDirectory);
        Assert.AreEqual(0xDEADBEEFu, root.VolumeSerial);
    }

    [TestMethod]
    public void ReadWindowsVolume_ThrowsIOExceptionNamingTheRootWhenTheQueryFails()
    {
        IndexedRoot.Dependencies._volumeSerialReader = _ => null;

        var failure = Assert.ThrowsExactly<IOException>(() => IndexedRoot.ReadWindowsVolume('Q'));

        Assert.Contains("Q", failure.Message);
    }

    [TestMethod]
    public void ReadWindowsVolume_RejectsDefaultKey()
    {
        Assert.ThrowsExactly<ArgumentException>(() => IndexedRoot.ReadWindowsVolume(default));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public void FromWindowsRoot_ReadsARealSerialForTheSystemRoot()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        var root = IndexedRoot.FromWindowsDrive(systemRoot[0]);

        Assert.AreNotEqual(0u, root.VolumeSerial);
        Assert.AreEqual(systemRoot, root.RootDirectory);
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void ReadWindowsVolume_OffWindowsWithoutTheSeam_ThrowsPlatformNotSupported()
    {
        var failure = Assert.ThrowsExactly<PlatformNotSupportedException>(() => IndexedRoot.ReadWindowsVolume('C'));

        Assert.Contains("GetVolumeInformationW", failure.Message);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void FromDirectory_ReadsTheContainingVolumeSerial()
    {
        Directory.CreateDirectory(TreeRoot);
        IndexedRoot.Dependencies._volumeSerialReader = root =>
        {
            Assert.AreEqual(Path.GetPathRoot(TreeRoot), root);
            return 0xDEADBEEF;
        };

        var root = IndexedRoot.FromDirectory(TreeRoot + Path.DirectorySeparatorChar);

        Assert.IsFalse(root.Root.IsVolume);
        Assert.AreEqual(TreeRoot, root.Root.ToString());
        Assert.AreEqual(TreeRoot, root.RootDirectory);
        Assert.AreEqual(0xDEADBEEFu, root.VolumeSerial);
        Assert.AreEqual(ProducerKind.Enumeration, root.Producer);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void FromDirectory_UnreadableContainingVolumeSerialMeansNone()
    {
        Directory.CreateDirectory(TreeRoot);
        IndexedRoot.Dependencies._volumeSerialReader = _ => null;

        var root = IndexedRoot.FromDirectory(TreeRoot);

        Assert.AreEqual(0u, root.VolumeSerial);
        Assert.AreEqual(TreeRoot, root.RootDirectory);
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void FromDirectory_OffWindowsHasNoVolumeSerial()
    {
        Directory.CreateDirectory(TreeRoot);
        IndexedRoot.Dependencies._volumeSerialReader = _ =>
            throw new AssertFailedException("The Windows serial reader must not run off Windows.");

        var root = IndexedRoot.FromDirectory(TreeRoot);

        Assert.AreEqual(0u, root.VolumeSerial);
        Assert.AreEqual(TreeRoot, root.RootDirectory);
    }

    [TestMethod]
    public async Task OpenAsync_DirectoryWithMftProducer_IsRejectedBeforeSideEffects()
    {
        Directory.CreateDirectory(TreeRoot);
        var invalid = CreateBypassedDirectoryMftRoot(TreeRoot);

        var failure = await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(
            options => options with { Root = invalid });

        Assert.Contains("volume root", failure.Message);
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
        var result = IndexedRoot.ReadVolumeVerdicts(new[] { 0 }, _ => @"X:\", _ => type,
            _ => type == DriveType.Fixed ? ready
                : throw new AssertFailedException("Non-fixed readiness was read."),
            _ => type == DriveType.Fixed && ready ? format
                : throw new AssertFailedException("Format was read without a ready fixed root."));

        var volume = result.Single();
        Assert.AreEqual(new VolumeKey('X'), volume.Volume);
        Assert.AreEqual(@"X:\", volume.RootDirectory);
        Assert.AreEqual(type, volume.DriveType);
        Assert.AreEqual(type == DriveType.Fixed && ready, volume.IsReady);
        Assert.AreEqual(type == DriveType.Fixed && ready ? format : null, volume.Format);
        Assert.AreEqual(reason is null, volume.IsIndexable);
        Assert.AreEqual(reason, volume.Reason);
    }

    [TestMethod]
    public void ReadVolumeVerdicts_ReportsVanishedRootNotReadyAndContinues()
    {
        var result = IndexedRoot.ReadVolumeVerdicts(new[] { 0, 1 },
            root => root == 0 ? @"Q:\" : @"C:\", _ => DriveType.Fixed,
            root => root == 0 ? throw new IOException("device vanished") : true, _ => "NTFS");

        Assert.HasCount(2, result);
        Assert.AreEqual(new VolumeKey('Q'), result[0].Volume);
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
        var valid = SyntheticIndexedRoot.Create(new VolumeKey('T'), @"T:\", 1);
        Assert.ThrowsExactly<ArgumentNullException>(() => SyntheticIndexedRoot.Create(new VolumeKey('T'), null!, 1));
        Assert.ThrowsExactly<ArgumentNullException>(() => valid with { RootDirectory = null! });
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t\r\n")]
    public void ConstructorAndWithInitializer_RejectEmptyOrWhiteSpaceRootDirectory(string blankRoot)
    {
        var valid = SyntheticIndexedRoot.Create(new VolumeKey('T'), @"T:\", 1);
        Assert.ThrowsExactly<ArgumentException>(() => SyntheticIndexedRoot.Create(new VolumeKey('T'), blankRoot, 1));
        Assert.ThrowsExactly<ArgumentException>(() => valid with { RootDirectory = blankRoot });
    }

    [TestMethod]
    [DataRow((ProducerKind)uint.MaxValue)]
    [DataRow(default(ProducerKind))]
    [DataRow((ProducerKind)999)]
    public void ConstructorAndWithInitializer_RejectUndefinedProducerKind(ProducerKind invalidPolicy)
    {
        var valid = SyntheticIndexedRoot.Create(new VolumeKey('T'), @"T:\", 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SyntheticIndexedRoot.Create(new VolumeKey('T'), @"T:\", 1, invalidPolicy));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => valid with { Producer = invalidPolicy });
    }

    [TestMethod]
    public void ConstructorAndWithInitializer_AcceptValidValues()
    {
        var root = SyntheticIndexedRoot.Create(new VolumeKey('t'), @"T:\root", 42, ProducerKind.Enumeration);
        Assert.AreEqual(new VolumeKey('T'), root.Root);
        Assert.AreEqual(@"T:\root", root.RootDirectory);
        Assert.AreEqual(42u, root.VolumeSerial);
        Assert.AreEqual(ProducerKind.Enumeration, root.Producer);

        var updated = root with
        {
            Root = new VolumeKey('u'),
            RootDirectory = @"U:\other",
            VolumeSerial = 99,
            Producer = ProducerKind.Mft
        };
        Assert.AreEqual(new VolumeKey('U'), updated.Root);
        Assert.AreEqual(@"U:\other", updated.RootDirectory);
        Assert.AreEqual(99u, updated.VolumeSerial);
        Assert.AreEqual(ProducerKind.Mft, updated.Producer);
    }

    [TestMethod]
    public async Task OpenAsync_RejectsNullRootBeforeSideEffects()
    {
        var failure = await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(
            options => options with { Root = null });

        Assert.AreEqual("options", failure.ParamName);
        Assert.Contains("FileIndexOptions.Root", failure.Message);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public async Task OpenAsync_RejectsBlankRootDirectoryBeforeSideEffects(string blankRoot)
    {
        await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(
            options => options with { Root = SyntheticIndexedRoot.Create(new VolumeKey('T'), blankRoot, 1, ProducerKind.Enumeration) });

        var bypassed = CreateBypassedRoot(new VolumeKey('T'), blankRoot, ProducerKind.Enumeration);
        await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(
            options => options with { Root = bypassed });
    }

    [TestMethod]
    [DataRow((ProducerKind)uint.MaxValue)]
    [DataRow(default(ProducerKind))]
    [DataRow((ProducerKind)999)]
    public async Task OpenAsync_RejectsUndefinedProducerKindBeforeSideEffects(ProducerKind invalidPolicy)
    {
        Directory.CreateDirectory(TreeRoot);
        await AssertInvalidOpenCreatesNothingAsync<ArgumentOutOfRangeException>(
            options => options with { Root = SyntheticIndexedRoot.Create(new VolumeKey('T'), TreeRoot, 1, invalidPolicy) });

        var bypassed = CreateBypassedRoot(new VolumeKey('T'), TreeRoot, invalidPolicy);
        await AssertInvalidOpenCreatesNothingAsync<ArgumentOutOfRangeException>(
            options => options with { Root = bypassed });
    }

    [TestMethod]
    public async Task OpenAsync_RejectsDefaultVolumeKeyBeforeSideEffects()
    {
        Directory.CreateDirectory(TreeRoot);
        var bypassed = CreateBypassedRoot(default, TreeRoot, ProducerKind.Enumeration);

        await AssertInvalidOpenCreatesNothingAsync<ArgumentException>(
            options => options with { Root = bypassed });
    }

    [TestMethod]
    public async Task OpenAsync_InvalidConfigurationLeavesAnExistingOwnedCacheDirectoryUntouched()
    {
        Directory.CreateDirectory(CacheDirectoryPath);
        var existingFile = Path.Combine(CacheDirectoryPath, "existing.txt");
        await File.WriteAllTextAsync(existingFile, "keep", TestContext.CancellationToken);
        var contentsBefore = SnapshotDirectory(CacheDirectoryPath);
        var protectionBefore = ReadProtection(CacheDirectoryPath);
        var producerCalls = 0;
        var progressCalls = 0;

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => FileIndex.OpenAsync(new FileIndexOptions
        {
            CacheMode = CacheMode.Persistent,
            CacheDirectory = CacheDirectoryPath,
            Root = null,
            MftSource = new MftIndexSource((_, _) =>
            {
                Interlocked.Increment(ref producerCalls);
                return Task.FromException<MftBlockProduceResult>(new InvalidOperationException("producer must not run"));
            }),
            ScanProgress = new SynchronousProgress<IndexScanProgress>(_ => Interlocked.Increment(ref progressCalls))
        }, CancellationToken.None));

        Assert.AreEqual(0, producerCalls);
        Assert.AreEqual(0, progressCalls);
        Assert.AreSequenceEqual(contentsBefore, SnapshotDirectory(CacheDirectoryPath), "no block slot, lock file or other entry may appear or vanish");
        Assert.AreEqual("keep", await File.ReadAllTextAsync(existingFile, TestContext.CancellationToken));
        Assert.AreEqual(protectionBefore, ReadProtection(CacheDirectoryPath));
    }

    static IndexedRoot CreateBypassedDirectoryMftRoot(string rootDirectory)
    {
        var instance = (IndexedRoot)RuntimeHelpers.GetUninitializedObject(typeof(IndexedRoot));
        foreach (var field in typeof(IndexedRoot).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (field.FieldType == typeof(RootKey))
            {
                field.SetValue(instance, RootKey.FromDirectory(rootDirectory));
            }
            else if (field.FieldType == typeof(string))
            {
                field.SetValue(instance, rootDirectory);
            }
            else if (field.FieldType == typeof(ProducerKind))
            {
                field.SetValue(instance, ProducerKind.Mft);
            }
        }

        return instance;
    }

    static string ReadProtection(string directory) =>
        OperatingSystem.IsWindows()
            ? new DirectoryInfo(directory).GetAccessControl()
                .GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access)
            : File.GetUnixFileMode(directory).ToString();

    static string[] SnapshotDirectory(string directory) =>
        [.. Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(entry => $"{Path.GetRelativePath(directory, entry)}|{(File.Exists(entry) ? new FileInfo(entry).Length : -1)}")
            .Order(StringComparer.Ordinal)];

    async Task<TException> AssertInvalidOpenCreatesNothingAsync<TException>(
        Func<FileIndexOptions, FileIndexOptions> configure)
        where TException : Exception
    {
        var producerCalls = 0;
        var scanProgressCalls = 0;
        var baseOptions = new FileIndexOptions
        {
            CacheMode = CacheMode.Persistent,
            CacheDirectory = CacheDirectoryPath,
            MftSource = new MftIndexSource((_, _) =>
            {
                Interlocked.Increment(ref producerCalls);
                return Task.FromException<MftBlockProduceResult>(
                    new InvalidOperationException("producer must not run"));
            }),
            ScanProgress = new SynchronousProgress<IndexScanProgress>(_ => Interlocked.Increment(ref scanProgressCalls))
        };

        var exception = await Assert.ThrowsExactlyAsync<TException>(
            () => FileIndex.OpenAsync(configure(baseOptions), CancellationToken.None));

        Assert.AreEqual(0, producerCalls);
        Assert.AreEqual(0, scanProgressCalls);
        Assert.IsFalse(Directory.Exists(CacheDirectoryPath));
        return exception;
    }

    static IndexedRoot CreateBypassedRoot(RootKey root, string? rootDirectory, ProducerKind policy)
    {
        var instance = (IndexedRoot)RuntimeHelpers.GetUninitializedObject(typeof(IndexedRoot));
        foreach (var field in typeof(IndexedRoot).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (field.FieldType == typeof(RootKey))
            {
                field.SetValue(instance, root);
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

    public TestContext TestContext { get; set; } = null!;
}
