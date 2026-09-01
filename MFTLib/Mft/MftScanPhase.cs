namespace MFTLib;

/// <summary>
///     Identifies the phase of an MFT volume scan.
/// </summary>
public enum MftScanPhase : byte
{
    /// <summary>
    ///     Scanning and parsing raw MFT record structures from disk.
    /// </summary>
    Parsing = 0,

    /// <summary>
    ///     Resolving full directory paths across parsed entries.
    /// </summary>
    ResolvingPaths = 1
}
