using System.Runtime.InteropServices;
using MFTLib.NativeInterop;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Scripts the shared <c>Kernel32.Hooks._deviceIoControl</c> seam for USN journal queries
///     (<c>UsnJournalVolumeInterop.QueryJournalData</c>), which <c>UsnJournalVolume</c> and
///     <c>FileIndex.QueryUsnJournalSettings</c> both issue. Classes that install a fake must
///     call <see cref="Reset" /> in their cleanup.
/// </summary>
static class JournalQueryFake
{
    internal static DeviceIoControl Create(Func<UsnJournalVolumeInterop.UsnJournalDataV0> reply)
    {
        return Fake;

        bool Fake(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
            IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            if (ioControlCode != UsnJournalVolumeInterop.FsctlQueryUsnJournal)
            {
                throw new InvalidOperationException($"Unexpected control code 0x{ioControlCode:X}.");
            }

            Marshal.StructureToPtr(reply(), outBuffer, false);
            bytesReturned = (uint)Marshal.SizeOf<UsnJournalVolumeInterop.UsnJournalDataV0>();
            return true;
        }
    }

    /// <summary>Answers every query with the reply the callback returns for that call.</summary>
    public static void Serve(Func<UsnJournalVolumeInterop.UsnJournalDataV0> reply)
    {
        Kernel32.Hooks._deviceIoControl = Create(reply);
    }

    /// <summary>Answers every query with one journal state.</summary>
    public static void Serve(ulong journalIdentifier = 7, long nextUsn = 200, ulong maximumSize = 0,
        ulong allocationDelta = 0)
    {
        Serve(() => Describe(journalIdentifier, nextUsn, maximumSize, allocationDelta));
    }

    /// <summary>Makes every query fail with the given Win32 error.</summary>
    public static void Fail(int win32Error)
    {
        bool Fake(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
            IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped)
        {
            // The production code reads Marshal.GetLastWin32Error, the managed P/Invoke error slot,
            // which a fake delegate has to set explicitly.
            Marshal.SetLastPInvokeError(win32Error);
            bytesReturned = 0;
            return false;
        }

        Kernel32.Hooks._deviceIoControl = Fake;
    }

    public static UsnJournalVolumeInterop.UsnJournalDataV0 Describe(ulong journalIdentifier, long nextUsn,
        ulong maximumSize, ulong allocationDelta)
    {
        return new UsnJournalVolumeInterop.UsnJournalDataV0
        {
            UsnJournalIdentifier = journalIdentifier,
            NextUsn = nextUsn,
            MaximumSize = maximumSize,
            AllocationDelta = allocationDelta
        };
    }

    public static void Reset()
    {
        Kernel32.Hooks.ResetToDefaults();
    }
}
