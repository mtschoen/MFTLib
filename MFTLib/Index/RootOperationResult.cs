namespace MFTLib.Index;

/// <summary>
///     The verdict of a <see cref="FileIndex" /> lifecycle call on its root. Operational failures
///     are reported as <see cref="Failed" /> rather than thrown.
/// </summary>
public enum RootOperationOutcome
{
    /// <summary>Nothing failed and nothing was skipped: the root's operation ran to its end.</summary>
    Succeeded,

    /// <summary>The operation failed for the root; the result carries the exception.</summary>
    Failed,

    /// <summary>
    ///     The operation does not apply to the volume: it has no MFT-backed block (start), or it is
    ///     not watching (stop and catch-up wait).
    /// </summary>
    NotApplicable
}

/// <summary>
///     The outcome and optional failure exception of a <see cref="FileIndex" /> lifecycle call on
///     its root.
/// </summary>
public sealed record RootOperationResult
{
    /// <summary>Creates the result of one root's operation.</summary>
    /// <param name="root">The normalized root key.</param>
    /// <param name="outcome">What the operation did for the root.</param>
    /// <param name="failure">The per-root exception for a failure; otherwise null.</param>
    internal RootOperationResult(RootKey root, RootOperationOutcome outcome, Exception? failure)
    {
        Root = root;
        Outcome = outcome;
        Failure = failure;
    }

    /// <summary>Matches the request key; assigning the invalid default throws ArgumentException.</summary>
    public RootKey Root { get; init => field = value.Validated(); }

    /// <summary>What the operation did for the root.</summary>
    public RootOperationOutcome Outcome { get; init; }

    /// <summary>
    ///     The per-root exception when <see cref="Outcome" /> is
    ///     <see cref="RootOperationOutcome.Failed" />; otherwise null. For a stop, the fault that had
    ///     ended the root's watch.
    /// </summary>
    public Exception? Failure { get; init; }
}
