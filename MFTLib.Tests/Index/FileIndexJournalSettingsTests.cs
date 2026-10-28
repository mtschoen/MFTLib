using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.QueryUsnJournalSettings" />: refusal for sources without live journal settings
///     and delegation to the query engine. The engine itself is swapped out through
///     <c>UsnJournalSettingsQuery.Dependencies._queryOverride</c>, so these run on every platform
///     through <see cref="FileIndex.ReadUsnJournalSettings" />, the logic behind the Windows-only entry point.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexJournalSettingsTests : IndexTreeTestBase
{
    protected override bool SeedDocuments => false;

    [TestCleanup]
    public void ResetQueryOverride() => UsnJournalSettingsQuery.Dependencies.ResetToDefaults();

    public override void Dispose()
    {
        try
        {
            JournalQueryFake.Reset();
            FileUtilities.Hooks.ResetToDefaults();
        }
        finally
        {
            base.Dispose();
        }
    }

    Task<FileIndex> OpenIndexAsync() =>
        FileIndex.OpenAsync(Fixture.Options(producer: ProducerSelection.MftOnly), CancellationToken.None);

    [TestMethod]
    public async Task QueryUsnJournalSettings_ConfiguredRoot_ReturnsEngineResult()
    {
        var expected = new UsnJournalSettings
        {
            MaximumSize = 64L * 1024 * 1024,
            AllocationDelta = 8L * 1024 * 1024
        };
        UsnJournalSettingsQuery.Dependencies._queryOverride =
            volume => volume == 'T'
                ? expected
                : throw new IOException($"Unexpected volume {volume}");
        await using var index = await OpenIndexAsync();

        // Lowercase input normalizes to the configured uppercase letter.
        var settings = index.ReadUsnJournalSettings();

        Assert.AreEqual(expected.MaximumSize, settings.MaximumSize);
        Assert.AreEqual(expected.AllocationDelta, settings.AllocationDelta);
    }

    /// <summary>
    ///     Both public entry points read the journal through the one query path, so one scripted
    ///     journal state yields identical settings from either. The index opens the real volume-root
    ///     handle on C: and the volume gets a fake handle; only the control request is scripted.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task QueryUsnJournalSettings_VolumeAndIndex_ReportTheSameSettingsForOneJournalState()
    {
        FileUtilities.Hooks._getVolumeHandle = _ => new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(1), false);
        JournalQueryFake.Serve(journalIdentifier: 9, nextUsn: 77, maximumSize: 0x08000000, allocationDelta: 0x01000000);
        await using var index = await FileIndex.OpenAsync(Fixture.Options('C', producer: ProducerSelection.MftOnly), CancellationToken.None);
        using var volume = MftVolume.Open('C');

        var fromVolume = volume.Journal.QueryUsnJournalSettings();
        var fromIndex = index.QueryUsnJournalSettings();

        Assert.AreEqual(fromVolume, fromIndex);
        Assert.AreEqual(0x08000000L, fromVolume.MaximumSize);
        Assert.AreEqual(0x01000000L, fromVolume.AllocationDelta);
    }

    [TestMethod]
    public async Task QueryUsnJournalSettings_WalkedRoot_RefusesAndNamesTheRoot()
    {
        UsnJournalSettingsQuery.Dependencies._queryOverride = _ => throw new AssertFailedException("A walked root has no journal.");
        await using var index = await FileIndex.OpenAsync(Fixture.Options(producer: ProducerSelection.WalkOnly), CancellationToken.None);

        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => index.ReadUsnJournalSettings());

        Assert.Contains("Root T", failure.Message);
        Assert.Contains("walked", failure.Message);
    }

    [TestMethod]
    public async Task QueryUsnJournalSettings_DisposedIndex_ThrowsObjectDisposedException()
    {
        var index = await OpenIndexAsync();
        await index.DisposeAsync();

        Assert.ThrowsExactly<ObjectDisposedException>(() => index.ReadUsnJournalSettings());
    }
}
