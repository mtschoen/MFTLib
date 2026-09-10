namespace MFTLib.Index;

/// <summary>Which producer builds each drive's block.</summary>
public enum ProducerPolicy
{
    /// <summary>
    ///     Build every drive's block with <see cref="FileIndexOptions.MftProducer" />. A drive
    ///     whose producer fails is reported as <see cref="DriveState.Failed" /> and gets no
    ///     block; it is never rebuilt by a directory walk instead.
    /// </summary>
    Mft,

    /// <summary>
    ///     Build every drive's block by walking its directory tree, ignoring
    ///     <see cref="FileIndexOptions.MftProducer" /> entirely. The only policy that reads the
    ///     filesystem recursively, and it is never selected on a caller's behalf.
    /// </summary>
    Enumeration
}
