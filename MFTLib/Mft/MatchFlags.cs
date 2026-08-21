using System.Diagnostics.CodeAnalysis;

namespace MFTLib;

[Flags]
[SuppressMessage("Naming", "CA1711", Justification = "Flags suffix is conventional here; renaming breaks consumers.")]
public enum MatchFlags : uint
{
    None = 0,
    ExactMatch = 1,
    Contains = 2,
    ResolvePaths = 4
}
