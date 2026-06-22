namespace MFTLib;

[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "MatchFlags is a [Flags] enum on the public API; the Flags suffix is conventional for flag enums and renaming would break consumers.")]
public enum MatchFlags : uint
{
    None = 0,
    ExactMatch = 1,
    Contains = 2,
    ResolvePaths = 4,
}
