using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>Reads the two components of a <see cref="CacheTag" />, which the type keeps internal.</summary>
public static class SyntheticCacheTag
{
    /// <summary>Gets the consumer-defined version the tag was constructed with.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The version; zero for the default tag.</returns>
    public static uint GetVersion(CacheTag tag) => tag.Version;

    /// <summary>Gets the four-character code the tag was constructed with.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Four ASCII characters; four NUL characters for the default tag.</returns>
    public static string GetFourCharacterCode(CacheTag tag) => tag.FourCc;
}
