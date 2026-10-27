namespace MFTLib.Index;

/// <summary>
///     The identity of an NTFS volume: an ASCII drive letter normalized to upper case.
///     The default value is invalid; reading its letter or formatting it throws <see cref="ArgumentException" />.
/// </summary>
public readonly record struct VolumeKey
{
    /// <summary>Creates a volume key from an ASCII letter in either case.</summary>
    /// <exception cref="ArgumentException"><paramref name="letter" /> is not an ASCII letter.</exception>
    public VolumeKey(char letter)
    {
        if (!char.IsAsciiLetter(letter))
        {
            throw new ArgumentException("The volume key must be an ASCII letter.", nameof(letter));
        }
        Letter = char.ToUpperInvariant(letter);
    }

    /// <summary>
    ///     Parses one ASCII drive letter with an optional colon and an optional final slash or backslash,
    ///     ignoring surrounding whitespace and letter case.
    /// </summary>
    /// <param name="text">The root spec to parse.</param>
    /// <param name="result">The normalized key on success; the invalid default key on failure.</param>
    /// <returns>True for a valid root spec; false for null or invalid input.</returns>
    public static bool TryParse(string? text, out VolumeKey result)
    {
        result = default;
        var token = text.AsSpan().Trim();
        if (token.IsEmpty || !char.IsAsciiLetter(token[0]))
        {
            return false;
        }

        var position = 1;
        if (position < token.Length && token[position] == ':')
        {
            position++;
        }
        if (position < token.Length && token[position] is '\\' or '/')
        {
            position++;
        }
        if (position != token.Length)
        {
            return false;
        }

        result = new VolumeKey(token[0]);
        return true;
    }

    /// <summary>The normalized ASCII letter. Rejects the default key.</summary>
    public char Letter => field != '\0'
        ? field
        : throw new ArgumentException("The default volume key is invalid.");

    /// <summary>Creates a normalized key from an ASCII letter.</summary>
    public static implicit operator VolumeKey(char letter) => new(letter);

    /// <summary>Returns the normalized letter. Rejects the default key.</summary>
    public override string ToString() => Letter.ToString();

    internal VolumeKey Validated()
    {
        _ = Letter;
        return this;
    }
}
