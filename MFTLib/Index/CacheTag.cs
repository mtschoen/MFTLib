using System.Globalization;
using System.Text.Json;

namespace MFTLib.Index;

/// <summary>An opaque consumer cache identity. The zero value means unspecified.</summary>
public readonly record struct CacheTag
{
    /// <summary>Initializes a consumer identity from a four-character ASCII code and version.</summary>
    /// <param name="fourCharacterCode">Exactly four ASCII characters identifying the consumer.</param>
    /// <param name="version">Consumer-defined version, including zero.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fourCharacterCode" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fourCharacterCode" /> is not exactly four ASCII characters.</exception>
    public CacheTag(string fourCharacterCode, uint version)
    {
        ArgumentNullException.ThrowIfNull(fourCharacterCode);
        if (fourCharacterCode.Length != 4 || fourCharacterCode.Any(character => character > 0x7f))
        {
            throw new ArgumentException("A cache FourCC must contain exactly four ASCII characters.", nameof(fourCharacterCode));
        }
        PackedFourCc = fourCharacterCode[0] | ((uint)fourCharacterCode[1] << 8) |
            ((uint)fourCharacterCode[2] << 16) | ((uint)fourCharacterCode[3] << 24);
        Version = version;
    }

    CacheTag(uint packedFourCc, uint version)
    {
        PackedFourCc = packedFourCc;
        Version = version;
    }

    internal uint PackedFourCc { get; }
    /// <summary>Consumer-defined cache identity version.</summary>
    internal uint Version { get; }
    /// <summary>Four-character ASCII code reconstructed from the packed on-disk representation.</summary>
    internal string FourCc => new(new[]
    {
        (char)(PackedFourCc & 0xff), (char)((PackedFourCc >> 8) & 0xff),
        (char)((PackedFourCc >> 16) & 0xff), (char)((PackedFourCc >> 24) & 0xff)
    });

    internal static CacheTag FromStorage(uint packedFourCc, uint version) => new(packedFourCc, version);

    /// <summary>
    ///     True when every packed byte is within the ASCII range the public constructor
    ///     enforces. On-disk bytes are untrusted, so a validator checks this before treating a
    ///     stored FourCC as a well-formed <see cref="CacheTag" />: a raw four-byte value read
    ///     off disk can hold anything, and <see cref="FromStorage" /> itself does not re-check
    ///     the constructor's invariant.
    /// </summary>
    internal static bool IsValidStorage(uint packedFourCc) => (packedFourCc & 0x80808080) == 0;

    /// <summary>Formats the identity as a quoted FourCC followed by its invariant-culture version.</summary>
    /// <returns>A diagnostic representation of this cache identity.</returns>
    public override string ToString() =>
        $"{JsonSerializer.Serialize(FourCc)} v{Version.ToString(CultureInfo.InvariantCulture)}";
}
