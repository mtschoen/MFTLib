namespace MFTLib;

/// <summary>
///     Identifies the phase of a broker drive scan.
/// </summary>
public enum BrokerScanPhase : byte
{
    /// <summary>
    ///     Scanning and parsing raw MFT record structures from disk.
    /// </summary>
    Parsing = 0,

    /// <summary>
    ///     Resolving full directory paths across parsed entries.
    /// </summary>
    ResolvingPaths = 1,

    /// <summary>
    ///     Transferring records into shared memory for the client.
    /// </summary>
    Transferring = 2
}
