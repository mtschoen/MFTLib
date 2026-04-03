namespace MFTLib;

[Flags]
public enum MatchFlags : uint
{
    None = 0,
    ExactMatch = 1,
    Contains = 2,
    ResolvePaths = 4,
}
