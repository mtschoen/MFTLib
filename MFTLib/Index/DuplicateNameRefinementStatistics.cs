namespace MFTLib.Index;

/// <summary>
///     What <see cref="DuplicateNameFinder.Find" /> observed while narrowing the candidate set,
///     exposed so a test can assert the refinement chain actually shrinks rather than only
///     asserting the final grouped result.
/// </summary>
internal readonly struct DuplicateNameRefinementStatistics
{
    internal DuplicateNameRefinementStatistics(IReadOnlyList<long> candidatesPerPass, long namesMaterialized)
    {
        CandidatesPerPass = candidatesPerPass;
        NamesMaterialized = namesMaterialized;
    }

    /// <summary>
    ///     How many rows each completed pass admitted, in pass order; index 0 is the initial
    ///     sieve pass over every live row, and each later entry is a refinement pass over only
    ///     the rows every earlier pass admitted.
    /// </summary>
    internal IReadOnlyList<long> CandidatesPerPass { get; }

    /// <summary>How many rows reached the final materialization pass and paid for a name string.</summary>
    internal long NamesMaterialized { get; }
}
