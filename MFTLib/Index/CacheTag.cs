using System.Globalization;
using System.Text.Json;

namespace MFTLib.Index;

/// <summary>An opaque consumer cache identity. The zero value means unspecified.</summary>
public readonly record struct CacheTag
{
    public CacheTag(string fourCc, uint version)
    {
        ArgumentNullException.ThrowIfNull(fourCc);
        if (fourCc.Length != 4 || fourCc.Any(character => character > 0x7f))
        {
            throw new ArgumentException("A cache FourCC must contain exactly four ASCII characters.", nameof(fourCc));
        }
        PackedFourCc = fourCc[0] | ((uint)fourCc[1] << 8) |
            ((uint)fourCc[2] << 16) | ((uint)fourCc[3] << 24);
        Version = version;
    }

    CacheTag(uint packedFourCc, uint version)
    {
        PackedFourCc = packedFourCc;
        Version = version;
    }

    internal uint PackedFourCc { get; }
    public uint Version { get; }
    public string FourCc => new(new[]
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

    public override string ToString() =>
        $"{JsonSerializer.Serialize(FourCc)} v{Version.ToString(CultureInfo.InvariantCulture)}";
}
