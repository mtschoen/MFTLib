using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.QueryUsnJournalSettings" />: drive membership validation and
///     delegation to the query engine. The engine itself is swapped out through
///     <c>UsnJournalSettingsQuery.Dependencies._queryOverride</c>, so these run on every platform
///     (the <see cref="SupportedOSPlatformAttribute" /> on each test satisfies CA1416 and
///     is inert at runtime, same as <c>IndexedDriveTests</c>).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexJournalSettingsTests : IDisposable
{
    IndexTreeFixture _fixture = null!;

    [TestInitialize]
    public void Initialize() => _fixture = new IndexTreeFixture(seedDocuments: false);

    [TestCleanup]
    [SupportedOSPlatform("windows")]
    public void ResetQueryOverride() => UsnJournalSettingsQuery.Dependencies.ResetToDefaults();

    public void Dispose()
    {
        JournalQueryFake.Reset();
        FileUtilities.Hooks.ResetToDefaults();
        _fixture.Dispose();
    }

    Task<FileIndex> OpenIndexAsync() =>
        FileIndex.OpenAsync(_fixture.Options(), CancellationToken.None);

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task QueryUsnJournalSettings_ConfiguredDrive_ReturnsEngineResult()
    {
        var expected = new UsnJournalSettings
        {
            MaximumSize = 64L * 1024 * 1024,
            AllocationDelta = 8L * 1024 * 1024
        };
        UsnJournalSettingsQuery.Dependencies._queryOverride =
            drive => drive == 'T'
                ? expected
                : throw new IOException($"Unexpected drive {drive}");
        await using var index = await OpenIndexAsync();

        // Lowercase input normalizes to the configured uppercase letter.
        var settings = index.QueryUsnJournalSettings('t');

        Assert.AreEqual(expected.MaximumSize, settings.MaximumSize);
        Assert.AreEqual(expected.AllocationDelta, settings.AllocationDelta);
    }

    /// <summary>
    ///     Both public entry points read the journal through the one query path, so one scripted
    ///     journal state yields identical settings from either. The index opens the real volume-root
    ///     handle on C: and the volume gets a fake handle; only the control request is scripted.
    /// </summary>
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task QueryUsnJournalSettings_VolumeAndIndex_ReportTheSameSettingsForOneJournalState()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The index query opens a Windows volume-root handle.");
            return;
        }

        FileUtilities.Hooks._getVolumeHandle = _ => new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(1), false);
        JournalQueryFake.Serve(journalIdentifier: 9, nextUsn: 77, maximumSize: 0x08000000, allocationDelta: 0x01000000);
        await using var index = await FileIndex.OpenAsync(_fixture.Options('C'), CancellationToken.None);
        using var volume = MftVolume.Open('C');

        var fromVolume = volume.Journal.QueryUsnJournalSettings();
        var fromIndex = index.QueryUsnJournalSettings('C');

        Assert.AreEqual(fromVolume, fromIndex);
        Assert.AreEqual(0x08000000L, fromVolume.MaximumSize);
        Assert.AreEqual(0x01000000L, fromVolume.AllocationDelta);
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
