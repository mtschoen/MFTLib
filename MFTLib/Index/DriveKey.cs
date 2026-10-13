namespace MFTLib.Index;

/// <summary>
///     An ASCII drive letter, normalized to upper case. The default value is invalid;
///     reading its letter, formatting it, or passing it to an index operation throws <see cref="ArgumentException" />.
/// </summary>
public readonly record struct DriveKey
{
    readonly char _letter;

    /// <summary>Creates a drive key from an ASCII letter in either case.</summary>
    /// <exception cref="ArgumentException"><paramref name="letter" /> is not an ASCII letter.</exception>
    public DriveKey(char letter)
    {
        if (!char.IsAsciiLetter(letter))
        {
            throw new ArgumentException("The drive key must be an ASCII letter.", nameof(letter));
        }
        _letter = char.ToUpperInvariant(letter);
    }

    /// <summary>The normalized ASCII letter. Rejects the default key.</summary>
    public char Letter => _letter != '\0'
        ? _letter
        : throw new ArgumentException("The default drive key is invalid.");

    /// <summary>Creates a normalized key from an ASCII letter.</summary>
    public static implicit operator DriveKey(char letter) => new(letter);

    /// <summary>Returns the normalized letter. Rejects the default key.</summary>
    public override string ToString() => Letter.ToString();

    internal DriveKey Validated()
    {
        _ = Letter;
        return this;
    }
}
