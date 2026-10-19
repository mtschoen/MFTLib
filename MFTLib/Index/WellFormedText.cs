namespace MFTLib.Index;

/// <summary>
///     The one text policy for strings that shape a cached block: retained names, pruned directory names and
///     pruned directory paths must be well-formed UTF-16. A lone surrogate has no UTF-8 form of its own, so
///     encoding it substitutes U+FFFD, and the scan-shape fingerprint could not tell the two spellings apart.
///     Admission rejects it before any cache work.
/// </summary>
internal static class WellFormedText
{
    /// <summary>Whether every surrogate in <paramref name="value" /> belongs to a high-then-low pair.</summary>
    /// <param name="value">The text to check.</param>
    /// <returns>True when the text has no unpaired surrogate.</returns>
    public static bool IsWellFormed(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
            }
            else if (char.IsSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }
}
