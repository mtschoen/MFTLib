namespace MFTLib.Index;

/// <summary>
///     Holds one root scan's samples to the order <see cref="IndexScanPhase" /> promises: a sample that would move the
///     scan back to an earlier phase, or lower the count within a phase, is dropped. A producer that re-runs a scan into
///     a larger block reports from zero again, so its samples are dropped until they pass the count already reported.
///     The <see cref="IndexScanPhase.Finished" /> sample always passes and ends the scan.
/// </summary>
sealed class ScanProgressOrder
{
    readonly Lock _lock = new();
    IndexScanPhase? _phase;
    uint _count;

    /// <summary>True when the sample keeps the scan's order, which it then advances.</summary>
    internal bool Admit(IndexScanProgress sample)
    {
        lock (_lock)
        {
            if (sample.Phase == IndexScanPhase.Finished)
            {
                _phase = null;
                _count = 0;
                return true;
            }

            if (_phase is { } phase &&
                (sample.Phase < phase || (sample.Phase == phase && sample.WrittenRowCount < _count)))
            {
                return false;
            }

            _phase = sample.Phase;
            _count = sample.WrittenRowCount;
            return true;
        }
    }
}
