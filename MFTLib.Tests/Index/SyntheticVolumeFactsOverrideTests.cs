using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public sealed class SyntheticVolumeFactsOverrideTests
{
    static readonly VolumeKey VolumeT = new('T');
    IDisposable? _scope;

    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestCleanup]
    public void Cleanup()
    {
        _scope?.Dispose();
        _scope = null;
        RootResolution.Hooks.VolumeFactsOverride = null;
    }

    [TestMethod]
    public async Task Override_SuppliesFactsToAnOpenThatNeverSawTheOptions()
    {
        using var fixture = new IndexTreeFixture();
        var treeRoot = fixture.TreeRoot;
        var options = Options(VolumeT);
        RootKey observedRoot = default;
        string? observedDirectory = null;
        using var scope = Override((root, rootDirectory) =>
        {
            observedRoot = root;
            observedDirectory = rootDirectory;
            return new SyntheticVolumeFactsResult(0x0BADF00D, isNtfs: false,
                volume: VolumeT, volumeRootDirectory: treeRoot);
        });

        await using var index = await FileIndex.OpenAsync(options, Token);

        Assert.AreEqual(VolumeT, observedRoot);
        Assert.AreEqual(@"T:\", observedDirectory);
        Assert.AreEqual(RootState.Ready, index.Status.State);
        Assert.AreEqual(0x0BADF00Du, index.Status.VolumeSerial);
        Assert.AreEqual(ProducerKind.Enumeration, index.Status.Producer);
        Assert.AreEqual(MftUnavailableReason.NotNtfs, index.Status.MftUnavailable);
        Assert.HasCount(1, index.Search(new SearchQuery("readme.md"), Token));
    }

    [TestMethod]
    public void NullResult_ReadsThePlatform()
    {
        using var temp = new TempDirectory("mftlib-volume-override-null");
        var options = Options(RootKey.FromDirectory(temp.Path));
        var expected = PlatformVolumeFacts.Read(options.Root!.Value, temp.Path);
        using var scope = Override((_, _) => null);

        var resolved = Resolve(options);

        AssertPlatformFacts(expected, resolved);
    }

    [TestMethod]
    public async Task NestedOrOverlappingOverride_ThrowsWithoutReplacingTheOwner()
    {
        using var scope = Override((_, _) => null);

        var failure = Assert.ThrowsExactly<InvalidOperationException>(
            () => SyntheticVolumeFacts.Override((_, _) => null));
        Assert.Contains("already active", failure.Message);
        await Task.Run(() => Assert.ThrowsExactly<InvalidOperationException>(
            () => SyntheticVolumeFacts.Override((_, _) => null)), Token);
        Assert.IsNotNull(RootResolution.Hooks.VolumeFactsOverride);
    }

    [TestMethod]
    public void DisposedScope_RestoresPlatformReads()
    {
        using var temp = new TempDirectory("mftlib-volume-override-dispose");
        var tempPath = temp.Path;
        var options = Options(RootKey.FromDirectory(tempPath));
        var expected = PlatformVolumeFacts.Read(options.Root!.Value, tempPath);
        var syntheticVolume = expected.Volume == VolumeT ? new VolumeKey('U') : VolumeT;
        var scope = Override((_, _) => new SyntheticVolumeFactsResult(expected.VolumeSerial ^ uint.MaxValue,
            volume: syntheticVolume, volumeRootDirectory: tempPath));

        scope.Dispose();
        var resolved = Resolve(options);

        AssertPlatformFacts(expected, resolved);
        scope.Dispose();
    }

    [TestMethod]
    public async Task Apply_TakesPrecedenceOverTheGlobalOverride()
    {
        using var fixture = new IndexTreeFixture();
        var treeRoot = fixture.TreeRoot;
        var options = SyntheticVolumeFacts.Apply(Options(VolumeT), 0x01234567, isNtfs: false,
            volume: VolumeT, volumeRootDirectory: treeRoot);
        var overrideCalled = false;
        using var scope = Override((_, _) =>
        {
            overrideCalled = true;
            return new SyntheticVolumeFactsResult(0x0BADF00D, volume: VolumeT,
                volumeRootDirectory: treeRoot);
        });

        await using var index = await FileIndex.OpenAsync(options, Token);

        Assert.AreEqual(RootState.Ready, index.Status.State);
        Assert.AreEqual(0x01234567u, index.Status.VolumeSerial);
        Assert.AreEqual(MftUnavailableReason.NotNtfs, index.Status.MftUnavailable);
        Assert.IsFalse(overrideCalled);
    }

    [TestMethod]
    public void NullReader_DoesNotClaimOwnership()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => SyntheticVolumeFacts.Override(null!));

        using var scope = Override((_, _) => null);

        Assert.IsNotNull(scope);
    }

    [TestMethod]
    public void RequireActivation_RefusesAProcessThatNeverAllowedOverrides()
    {
        var unactivated = new RootResolution.RootResolutionHooks();
        var refusal = Assert.ThrowsExactly<InvalidOperationException>(() => SyntheticVolumeFacts.RequireActivation(unactivated));
        StringAssert.Contains(refusal.Message, "AllowOverrides");

        unactivated.AllowVolumeFactsOverrides();
        unactivated.AllowVolumeFactsOverrides();
        SyntheticVolumeFacts.RequireActivation(unactivated);
    }

    [TestMethod]
    public void AllowOverrides_ActivatesTheProcessSeamAndIsIdempotent()
    {
        SyntheticVolumeFacts.AllowOverrides();
        SyntheticVolumeFacts.AllowOverrides();
        Assert.IsTrue(RootResolution.Hooks.VolumeFactsOverridesAllowed);
        SyntheticVolumeFacts.RequireActivation(RootResolution.Hooks);
    }

    IDisposable Override(Func<RootKey, string, SyntheticVolumeFactsResult?> reader) =>
        _scope = SyntheticVolumeFacts.Override(reader);

    static FileIndexOptions Options(RootKey root) => new()
    {
        Root = root,
        CacheMode = CacheMode.Disabled
    };

    static ResolvedRoot Resolve(FileIndexOptions options) =>
        RootResolution.Resolve(RootKind.PrepareOpenOptions(options));

    static void AssertPlatformFacts(VolumeFacts expected, ResolvedRoot resolved)
    {
        Assert.AreEqual(expected.Volume, resolved.Volume);
        Assert.AreEqual(expected.VolumeRootDirectory, resolved.VolumeRootDirectory);
        Assert.AreEqual(expected.VolumeSerial, resolved.VolumeSerial);
    }
}
