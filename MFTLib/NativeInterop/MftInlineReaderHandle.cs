using Microsoft.Win32.SafeHandles;

namespace MFTLib.NativeInterop;

/// <summary>
///     The native inline-content reader, freed exactly once however the owner is released. The reader borrows
///     the volume handle or dump input it was opened on, so dispose it before them.
/// </summary>
sealed class MftInlineReaderHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates the empty handle the native open fills in.</summary>
    public MftInlineReaderHandle() : base(true)
    {
    }

    protected override bool ReleaseHandle()
    {
        MFTLibNative.CloseMftInlineReader(handle);
        return true;
    }
}
