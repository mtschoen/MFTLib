using MFTLib.NativeInterop;

namespace MFTLib;

/// <summary>
///     The parsed slices of one scan, in record order, for <see cref="MftBlockRowWriter" />. Enumerating
///     <see cref="Slices" /> starts the scan; <see cref="Finish" /> waits for it to end and throws what stopped it;
///     disposing stops a scan the writer abandoned and releases what the source holds, such as the volume.
/// </summary>
interface IMftSliceSource : IDisposable
{
    /// <summary>How far the rows are trusted: a dump's are checked as they are written, a live volume's are not.</summary>
    MftRowValidation Validation { get; }

    /// <summary>The slices as they are parsed. The consumer returns each one once written.</summary>
    IEnumerable<MftParsedSlice> Slices();

    /// <summary>Waits for the scan to end after the last slice, then throws what stopped it.</summary>
    /// <returns>The records the scan left out and the extension data sizes to apply to the written rows.</returns>
    MftSliceCompletion Finish();
}

/// <summary>What a scan reports once its last slice is delivered.</summary>
/// <param name="RecordsOmitted">Allocated records the scan passed over because it could not trust them.</param>
/// <param name="ExtensionDataSizes">
///     Data sizes extension records carry for their base records; empty when the source applied them itself.
/// </param>
readonly record struct MftSliceCompletion(long RecordsOmitted, MftExtensionDataSize[] ExtensionDataSizes);
