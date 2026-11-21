using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.NativeInterop;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.Index.InlineContentIndexes;

namespace MFTLib.Tests.Index;

/// <summary>
///     A read through the broker carries the block's full 64-bit serial to the host, whose production read checks it
///     against the opened volume. The <c>FileUtilities.Hooks._getVolumeHandle</c> and <c>Kernel32.Hooks._deviceIoControl</c>
///     seams stand in for the volume, so the check runs unelevated.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexBrokerInlineContentSerialTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public void Cleanup()
    {
        FileUtilities.Hooks.ResetToDefaults();
        Kernel32.Hooks.ResetToDefaults();
        NativeTestHooks.NativeResetTestState();
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Read_WhenTheHostsVolumeSharesOnlyTheLowHalfOfTheBlocksSerial_FailsBeforeAnyAnswer()
    {
        using var image = new TempFile();
        await File.WriteAllBytesAsync(image.Path, new byte[8192], TestContext.CancellationToken);
        NativeTestHooks.NativeSetVolumeRecordSizeOverride(1024);
        var imagePath = image.Path;
        FileUtilities.Hooks._getVolumeHandle = _ => File.OpenHandle(imagePath);
        Kernel32.Hooks._deviceIoControl = (_, code, _, _, output, _, out returned, _) =>
        {
            Assert.AreEqual(0x00090064u, code, "FSCTL_GET_NTFS_VOLUME_DATA");
            Marshal.StructureToPtr(new NtfsVolumeDataBufferNative { VolumeSerialNumber = 0x3333333322222222 }, output, false);
            returned = (uint)Marshal.SizeOf<NtfsVolumeDataBufferNative>();
            return true;
        };
        ulong? hostSerial = null;
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ReadInlineContent = (volume, serial, requests, sink, cancellationToken) =>
            {
                hostSerial = serial;
                LiveVolumeSources.ReadInlineContent(volume, serial, requests, sink, cancellationToken);
            }
        }));
        var process = broker.Process;
        using var directory = new TempDirectory();
        await using var index = await OpenVolumeAsync(new BrokerInlineContentReader(_ => Task.FromResult(process)),
            0x1111111122222222, directory.Path);
        var answers = 0;

        var result = await index.ReadInlineContentAsync([Entry(index, 7), Entry(index, 6), Entry(index, 10)],
            _ => answers++, CancellationToken.None).WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(0x1111111122222222UL, hostSerial);
        Assert.AreEqual(RootOperationOutcome.Failed, result.Outcome);
        Assert.AreEqual($"Volume {DriveLetter} does not have the expected serial.", result.Failure!.Message);
        Assert.AreEqual(0, answers);
    }
}
