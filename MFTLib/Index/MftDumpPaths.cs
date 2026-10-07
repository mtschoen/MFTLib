namespace MFTLib.Index;

/// <summary>
///     The path rules of an MFT dump's virtual root <c>dump:/{driveLetter}</c>. A dump has no host
///     directory, so its paths are opaque strings: the separator is always <c>/</c>, a backslash is
///     accepted wherever a separator is read, and nothing here touches the filesystem or the
///     host's separator rules, which keeps a rendered path and a lookup of it agreeing on every
///     operating system.
/// </summary>
internal static class MftDumpPaths
{
    internal const char Separator = '/';

    const char Backslash = (char)0x5C;

    const string RootPrefix = "dump:/";

    /// <summary>The virtual root of the dump assigned <paramref name="driveLetter" />, in upper case.</summary>
    internal static string CanonicalRoot(char driveLetter) => RootPrefix + char.ToUpperInvariant(driveLetter);

    /// <summary>True when <paramref name="root" /> is exactly the canonical root of <paramref name="driveLetter" />.</summary>
    internal static bool IsCanonicalRoot(string? root, char driveLetter) =>
        string.Equals(root, CanonicalRoot(driveLetter), StringComparison.Ordinal);

    /// <summary>
    ///     The root as lookups compare it: backslashes folded to <c>/</c> and any trailing separator
    ///     removed. Null for a null or empty root, which can never match a path.
    /// </summary>
    internal static string? ToMatchablePrefix(string? root) =>
        root is { Length: > 0 } ? Fold(root).TrimEnd(Separator) : null;

    /// <summary>Reads backslashes as the virtual separator, the form lookups compare in.</summary>
    internal static string Fold(string path) => path.Replace(Backslash, Separator);

    /// <summary>True for the virtual separator and for a backslash, which lookups read as one.</summary>
    internal static bool IsSeparator(char character) => character is Separator or Backslash;

    /// <summary>Joins the root with a name chain, root first, using the one virtual separator.</summary>
    internal static string Join(string root, IReadOnlyList<string> segments)
    {
        var path = new System.Text.StringBuilder(root.TrimEnd(Separator));
        foreach (var segment in segments)
        {
            path.Append(Separator).Append(segment);
        }

        return path.ToString();
    }
}
