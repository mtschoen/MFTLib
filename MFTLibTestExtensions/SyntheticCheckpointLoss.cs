using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     Sets the raw journal positions of a <see cref="JournalCheckpointLoss" />, which the type
///     keeps internal. A fixture that compares a scripted producer's loss with the report an
///     index publishes needs the same positions on both, because report equality covers them.
/// </summary>
public static class SyntheticCheckpointLoss
{
    /// <summary>Copies a report with exact raw journal positions and every public value unchanged.</summary>
    /// <param name="report">The report to copy.</param>
    /// <param name="checkpointUsn">Where the drive's block left off.</param>
    /// <param name="firstUsn">The oldest USN the journal still retained.</param>
    /// <param name="nextUsn">The USN the journal's next record would be written at.</param>
    /// <returns>The copy; the derived sizes, the cause and the detection are not recomputed.</returns>
    public static JournalCheckpointLoss WithJournalPositions(JournalCheckpointLoss report, long checkpointUsn,
        long firstUsn, long nextUsn)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report with { CheckpointUsn = checkpointUsn, FirstUsn = firstUsn, NextUsn = nextUsn };
    }
}
