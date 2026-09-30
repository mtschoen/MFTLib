namespace MFTLib.Index;

/// <summary>
///     The verdict on one drive of a batched <see cref="FileIndex" /> call. One drive's failure is a verdict,
///     not a throw, so the other drives' results survive it.
/// </summary>
public enum DriveOperationOutcome
{
    /// <summary>Nothing failed and nothing was skipped: the drive's part ran to its end.</summary>
    Succeeded,

    /// <summary>The operation failed for the drive; the result carries the exception.</summary>
    Failed,

    /// <summary>
    ///     The operation does not apply to the drive: it has no MFT-backed block (start), or it is
    ///     not watching (stop and catch-up wait).
    /// </summary>
    NotApplicable
}

/// <summary>The result of one drive's part of a batched <see cref="FileIndex" /> operation.</summary>
/// <param name="DriveLetter">The drive, as the caller named it.</param>
/// <param name="Outcome">What the operation did for the drive.</param>
/// <param name="Failure">
///     The exception the single-drive form would have thrown when <paramref name="Outcome" /> is
///     <see cref="DriveOperationOutcome.Failed" />; otherwise null. For a stop, the fault that had
///     ended the drive's watch.
/// </param>
public sealed record DriveOperationResult(char DriveLetter, DriveOperationOutcome Outcome, Exception? Failure);
