using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MFTLib.Index;

public static partial class CacheDirectory
{
    const string BlockFileExtension = ".mlix";
    const int SerialDigits = 8;
    const int DirectoryHashHexDigits = 16;
    const char DirectoryStemPrefix = 'D';
    static readonly SearchValues<char> LowercaseHexDigits = SearchValues.Create("0123456789abcdef");

    /// <summary>
    ///     A block file name recognised by shape alone. <see cref="Volume" /> is set for a volume name, which carries
    ///     its letter; a directory name carries only a hash of its key, so its root is read from the block.
    /// </summary>
    internal readonly record struct ParsedBlockName(string Stem, RootKey? Volume, uint VolumeSerial);

    /// <summary>A listed block file; <see cref="Root" /> is null only for an unselected directory-shaped name.</summary>
    internal sealed record CacheCandidate(ParsedBlockName Parsed, string Path, long Size, DateTime LastWriteUtc,
        RootKey? Root)
    {
        internal CachedBlockFile ToFile(RootKey root) => new(root, Parsed.VolumeSerial, Path, Size, LastWriteUtc);
    }

    /// <summary>
    ///     Produces the canonical cache filename for a root. A volume uses its uppercase drive letter. A directory
    ///     uses <c>D</c> and the first sixteen lowercase hexadecimal digits of the SHA-256 of the key text encoded as
    ///     UTF-8, uppercased first on a case-insensitive platform so the name is case-stable. Eight uppercase
    ///     hexadecimal serial digits follow the stem.
    /// </summary>
    /// <param name="root">Root of the cached block.</param>
    /// <param name="volumeSerial">Serial number of the volume containing the root, encoded in the filename.</param>
    /// <returns>The filename without a directory component.</returns>
    internal static string BlockFileName(RootKey root, uint volumeSerial) =>
        $"{NameStem(root)}-{volumeSerial:X8}{BlockFileExtension}";

    /// <summary>The part of the file name before the dash: the letter for a volume, <c>D</c> plus the key hash for a directory.</summary>
    internal static string NameStem(RootKey root)
    {
        if (root.TryGetVolume(out var volume))
        {
            return volume.ToString();
        }

        var text = root.ToString();
        if (!RootKind.DirectoryWalk.IsCaseSensitive)
        {
            text = text.ToUpperInvariant();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return DirectoryStemPrefix + Convert.ToHexStringLower(hash.AsSpan(0, DirectoryHashHexDigits / 2));
    }

    /// <summary>
    ///     The exact inverse of <see cref="BlockFileName" /> as far as a name can go, and private to the library on
    ///     purpose: the format is the library's, not the consumer's. The final round trip through the formatter is
    ///     what keeps the two from ever drifting apart, so a name is only accepted when the formatter would have
    ///     produced exactly it.
    /// </summary>
    internal static bool TryParseBlockFileName(string fileName, out ParsedBlockName parsed)
    {
        parsed = default;
        if (fileName.Length == 1 + 1 + SerialDigits + BlockFileExtension.Length)
        {
            return TryParseVolumeName(fileName, out parsed);
        }

        return fileName.Length == 1 + DirectoryHashHexDigits + 1 + SerialDigits + BlockFileExtension.Length &&
               TryParseDirectoryName(fileName, out parsed);
    }

    static bool TryParseVolumeName(string fileName, out ParsedBlockName parsed)
    {
        parsed = default;
        if (fileName[1] != '-' || !char.IsAsciiLetter(fileName[0]) ||
            !TryParseSerial(fileName, 2, out var serial))
        {
            return false;
        }

        RootKey volume = new VolumeKey(fileName[0]);
        if (!string.Equals(BlockFileName(volume, serial), fileName, StringComparison.Ordinal))
        {
            return false;
        }

        parsed = new ParsedBlockName(volume.ToString(), volume, serial);
        return true;
    }

    static bool TryParseDirectoryName(string fileName, out ParsedBlockName parsed)
    {
        parsed = default;
        const int dashIndex = 1 + DirectoryHashHexDigits;
        var stem = fileName[..dashIndex];
        if (fileName[0] != DirectoryStemPrefix || fileName[dashIndex] != '-' ||
            stem.AsSpan(1).ContainsAnyExcept(LowercaseHexDigits) ||
            !TryParseSerial(fileName, dashIndex + 1, out var serial) ||
            !string.Equals($"{stem}-{serial:X8}{BlockFileExtension}", fileName, StringComparison.Ordinal))
        {
            return false;
        }

        parsed = new ParsedBlockName(stem, null, serial);
        return true;
    }

    static bool TryParseSerial(string fileName, int start, out uint serial) =>
        uint.TryParse(fileName.AsSpan(start, SerialDigits), NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out serial);
}
