using System.Collections.ObjectModel;

namespace MFTLib;

/// <summary>
///     A batch with no records that tells the block writer how many records a scan had to leave
///     out before any batch was built, so they are counted with the records the writer skips and
///     none disappears without a trace. A live scan yields one when the parser passed over
///     allocated records whose fixup was invalid.
/// </summary>
/// <param name="omittedCount">The records left out.</param>
internal sealed class MftOmittedRecords(long omittedCount) : ReadOnlyCollection<MftRecord>([])
{
    /// <summary>The records the scan left out, added to the write's skipped record count.</summary>
    public long OmittedCount { get; } = omittedCount;
}
