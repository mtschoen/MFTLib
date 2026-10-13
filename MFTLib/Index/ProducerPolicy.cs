namespace MFTLib.Index;

/// <summary>Which producer builds each drive's block.</summary>
public enum ProducerPolicy
{
    /// <summary>
    ///     Build every drive's block with <see cref="FileIndexOptions.MftSource" />. A drive
    ///     whose producer fails without a usable block is reported as <see cref="DriveState.Failed" />.
    ///     A failed replacement scan retains the existing block and reports <see cref="DriveState.Ready" />
    ///     with a producer failure; the drive is never rebuilt by a directory walk instead.
    /// </summary>
    Mft,

    /// <summary>
    ///     Build every drive's block by walking its directory tree, ignoring
    ///     <see cref="FileIndexOptions.MftSource" /> entirely. The only policy that reads the
    ///     filesystem recursively, and it is never selected on a caller's behalf.
    /// </summary>
    Enumeration
}
