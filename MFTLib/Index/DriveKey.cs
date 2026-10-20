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

    /// <summary>
    ///     Parses one ASCII drive letter with an optional colon and an optional final slash or backslash,
    ///     ignoring surrounding whitespace and letter case. Accepts drive specs, not paths beyond a root.
    /// </summary>
    /// <param name="text">The drive spec to parse; callers holding a path pass its root.</param>
    /// <param name="driveKey">The normalized key on success; the invalid default key on failure.</param>
    /// <returns>True for a valid drive spec; false for null or invalid input.</returns>
    public static bool TryParse(string? text, out DriveKey driveKey)
    {
        driveKey = default;
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

        driveKey = new DriveKey(token[0]);
        return true;
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
