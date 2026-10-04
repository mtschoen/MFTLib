using System.Diagnostics.CodeAnalysis;

namespace MFTLib;

/// <summary>Controls name matching and record materialization when reading the NTFS MFT.</summary>
[Flags]
[SuppressMessage("Naming", "CA1711", Justification = "Flags suffix is conventional here; renaming breaks consumers.")]
public enum MatchFlags : uint
{
    /// <summary>
    ///     Default flags: enables no optional matching or materialization behavior. If a name
    ///     filter is supplied, records do not match because neither exact nor substring
    ///     matching is selected.
    /// </summary>
    None = 0,
    /// <summary>Matches names exactly rather than by substring.</summary>
    ExactMatch = 1,
    /// <summary>Matches records whose names contain the requested text.</summary>
    Contains = 2,
    /// <summary>Resolves record paths when valid parent relationships are available.</summary>
    ResolvePaths = 4,

    /// <summary>
    ///     Include freed base records with <see cref="MftRecord.InUse" /> false. With
    ///     <see cref="ResolvePaths" />, every parent reference must name a directory and match its
    ///     sequence, or be one behind a freed parent's sequence; untrusted paths remain unresolved.
    /// </summary>
    IncludeFreed = 8
}
