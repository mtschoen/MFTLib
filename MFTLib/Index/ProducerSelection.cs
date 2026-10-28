namespace MFTLib.Index;

/// <summary>
///     Which producer an index may use to build its root's block, as <see cref="FileIndexOptions.Producer" />. The
///     choice is made once at open and fixed for the life of the index: <see cref="RootStatus.Producer" /> reports
///     what was selected. The default value of the enum is not a selection and is rejected at open.
/// </summary>
public enum ProducerSelection : uint
{
    /// <summary>
    ///     Scan the MFT when the volume is eligible (a ready, local NTFS volume with an MFT source), and otherwise walk
    ///     the directory tree, reporting the reason in <see cref="RootStatus.MftUnavailable" />. A broker that cannot
    ///     be launched at an open's cold scan falls back the same way. A scan that fails once the MFT producer started
    ///     fails the index and never walks.
    /// </summary>
    Automatic = 1,

    /// <summary>
    ///     Scan the MFT or fail with <see cref="RootFailureKind.MftUnavailable" />; never walk. The producer is
    ///     reported as <see cref="ProducerKind.Mft" /> even when the volume is ineligible, because the choice is fixed.
    /// </summary>
    MftOnly = 2,

    /// <summary>Walk the directory tree and never touch <see cref="FileIndexOptions.MftSource" />.</summary>
    WalkOnly = 3
}
