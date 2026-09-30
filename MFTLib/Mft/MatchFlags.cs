using System.Diagnostics.CodeAnalysis;

namespace MFTLib;

[Flags]
[SuppressMessage("Naming", "CA1711", Justification = "Flags suffix is conventional here; renaming breaks consumers.")]
public enum MatchFlags : uint
{
    None = 0,
    ExactMatch = 1,
    Contains = 2,
    ResolvePaths = 4,

    /// <summary>
    ///     Include freed base records with <see cref="MftRecord.InUse" /> false. With
    ///     <see cref="ResolvePaths" />, every parent reference must name a directory and match its
    ///     sequence, or be one behind a freed parent's sequence; untrusted paths remain unresolved.
    /// </summary>
    IncludeFreed = 8
}
