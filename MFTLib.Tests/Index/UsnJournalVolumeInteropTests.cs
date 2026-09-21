using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests.Index;

/// <summary>
///     The unelevated volume-root handle both index journal reads share, and what they do
///     when the volume refuses the IOCTL. The handle is a real unelevated one on C:, because
///     opening it is the part worth exercising for real; only the IOCTL is swapped out,
///     through <c>UsnJournalVolumeInterop._ioControl</c>, so a drive whose journal is healthy
///     can still stand in for one that is not.
/// </summary>
[TestClass]
[DoNotParallelize]
public class UsnJournalVolumeInteropTests
{
    const int ErrorJournalNotActive = 1179;

    [TestCleanup]
    [SupportedOSPlatform("windows")]
    public void Cleanup()
    {
        UsnJournalVolumeInterop.ResetToDefaults();
    }

    /// <summary>Fails every IOCTL whose control code matches, and serves the rest a reply.</summary>
    [SupportedOSPlatform("windows")]
    static void FailIoControl(uint failingCode, int lastError, byte[]? otherwise = null)
    {
        bool Fake(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
            IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            if (ioControlCode == failingCode)
            {
                // The production code reads Marshal.GetLastWin32Error, which is the managed
                // P/Invoke error slot - a fake delegate has to set it explicitly.
                Marshal.SetLastPInvokeError(lastError);
                bytesReturned = 0;
                return false;
            }

            var reply = otherwise ?? [];
            Marshal.Copy(reply, 0, outBuffer, reply.Length);
            bytesReturned = (uint)reply.Length;
            return true;
        }

        UsnJournalVolumeInterop._ioControl = Fake;
    }

    /// <summary>Serves one <c>USN_JOURNAL_DATA_V0</c> buffer to every query.</summary>
    [SupportedOSPlatform("windows")]
    static void ServeJournalData(byte[] journalData)
    {
        bool Fake(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
            IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            Marshal.Copy(journalData, 0, outBuffer, journalData.Length);
            bytesReturned = (uint)journalData.Length;
            return true;
        }

        UsnJournalVolumeInterop._ioControl = Fake;
    }

    /// <summary>A <c>USN_JOURNAL_DATA_V0</c> buffer: seven 8-byte fields.</summary>
    static byte[] BuildJournalData(ulong journalId = 0xABCD, long firstUsn = 1_000, long nextUsn = 4_600,
        ulong maximumSize = 128UL * 1024 * 1024, ulong allocationDelta = 16UL * 1024 * 1024)
    {
        var data = new byte[7 * 8];
        BinaryPrimitives.WriteUInt64LittleEndian(data, journalId);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), firstUsn);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), nextUsn);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(40), maximumSize);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(48), allocationDelta);
        return data;
    }

    static bool SkipOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        Assert.Inconclusive("Requires a Windows host to open a volume-root handle");
        return true;
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void SettingsQuery_MetadataIoctlFails_SurfacesTheWin32Error()
    {
        if (SkipOffWindows())
        {
            return;
        }

        FailIoControl(UsnJournalVolumeInterop.FsctlQueryUsnJournal, ErrorJournalNotActive);

        var exception = Assert.ThrowsException<Win32Exception>(() => UsnJournalSettingsQuery.Query('C'));
        Assert.AreEqual(ErrorJournalNotActive, exception.NativeErrorCode);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void QueryJournalData_ReadsEveryFieldTheCheckpointCheckNeeds()
    {
        if (SkipOffWindows())
        {
            return;
        }

        ServeJournalData(BuildJournalData(journalId: 0xFEED, firstUsn: 2_048, nextUsn: 9_216));

        using var handle = UsnJournalVolumeInterop.OpenVolumeRoot('C', "read its journal for a test");
        var journal = UsnJournalVolumeInterop.QueryJournalData(handle);

        Assert.AreEqual(0xFEEDul, journal.UsnJournalId);
        Assert.AreEqual(2_048L, journal.FirstUsn);
        Assert.AreEqual(9_216L, journal.NextUsn);
        Assert.AreEqual(128UL * 1024 * 1024, journal.MaximumSize);
        Assert.AreEqual(16UL * 1024 * 1024, journal.AllocationDelta);
    }

    /// <summary>
    ///     A volume that refuses the journal query is not evidence that a cached checkpoint is
    ///     unusable, so the check reports nothing rather than forcing a rescan over it.
    /// </summary>
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void CheckpointCheck_JournalQueryRefused_ReportsNothing()
    {
        if (SkipOffWindows())
        {
            return;
        }

        FailIoControl(UsnJournalVolumeInterop.FsctlQueryUsnJournal, ErrorJournalNotActive);

        Assert.IsNull(JournalCheckpointCheck.Check('C', checkpointJournalId: 0xABCD, checkpointUsn: 10));
    }

    /// <summary>
    ///     The check against a real volume, through the real unelevated handle. C:'s journal
    ///     id is never zero, so a checkpoint claiming journal 0 is always a recreated journal.
    /// </summary>
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void CheckpointCheck_AgainstTheRealVolume_ReadsItsLiveJournal()
    {
        if (SkipOffWindows())
        {
            return;
        }

        var loss = JournalCheckpointCheck.Check('C', checkpointJournalId: 0, checkpointUsn: 0);

        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossCause.JournalRecreated, loss.Cause);
        Assert.AreEqual('C', loss.DriveLetter);
        Assert.IsTrue(loss.NextUsn >= loss.FirstUsn, "the journal window should not run backwards");
        Assert.IsTrue(loss.AllocationDelta > 0);
        Assert.IsTrue(loss.MaximumSize > 0);
        Assert.IsNull(loss.SizeThatWouldHaveRetained, "no size would have kept a different journal's checkpoint");
        Assert.IsNull(loss.BytesBehind);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void ResetToDefaults_RestoresTheRealIoctl()
    {
        if (SkipOffWindows())
        {
            return;
        }

        FailIoControl(UsnJournalVolumeInterop.FsctlQueryUsnJournal, ErrorJournalNotActive);
        UsnJournalVolumeInterop.ResetToDefaults();

        // Back on the real IOCTL, C: answers its settings again.
        Assert.IsTrue(UsnJournalSettingsQuery.Query('C').MaximumSize > 0);
    }
}
