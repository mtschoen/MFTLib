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
public sealed record DriveOperationResult
{
    /// <summary>Creates the result of one drive's operation.</summary>
    /// <param name="driveLetter">The normalized drive key.</param>
    /// <param name="outcome">What the operation did for the drive.</param>
    /// <param name="failure">The per-drive exception for a failure; otherwise null.</param>
    internal DriveOperationResult(DriveKey driveLetter, DriveOperationOutcome outcome, Exception? failure)
    {
        Drive = driveLetter;
        Outcome = outcome;
        Failure = failure;
    }

    /// <summary>Matches the request key; assigning the invalid default throws ArgumentException.</summary>
    public DriveKey Drive { get; init => field = value.Validated(); }

    /// <summary>What the operation did for the drive.</summary>
    public DriveOperationOutcome Outcome { get; init; }

    /// <summary>
    ///     The per-drive exception when <see cref="Outcome" /> is
    ///     <see cref="DriveOperationOutcome.Failed" />; otherwise null. For a stop, the fault that had
    ///     ended the drive's watch.
    /// </summary>
    public Exception? Failure { get; init; }
}
