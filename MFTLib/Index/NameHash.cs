namespace MFTLib.Index;

/// <summary>Stable FNV-1a 64 over the UTF-16LE units after <see cref="NameFolding"/>, low byte then high byte.</summary>
static class NameHash
{
    const ulong OffsetBasis = 14695981039346656037UL;
    const ulong Prime = 1099511628211UL;

    internal static ulong Compute(ReadOnlySpan<char> name)
    {
        var hash = OffsetBasis;
        for (var index = 0; index < name.Length;)
        {
            var consumed = NameFolding.FoldNext(name, index, out var first, out var second);
            AddUnit(ref hash, first);
            if (consumed == 2)
            {
                AddUnit(ref hash, second);
            }

            index += consumed;
        }

        return hash;
    }

    /// <summary>Hashes a stored name; an ASCII name folds byte by byte with no surrogate handling.</summary>
    internal static ulong Compute(NameView name)
    {
        if (name.IsUtf16)
        {
            return Compute(name.Utf16Characters);
        }

        var hash = OffsetBasis;
        foreach (var value in name.AsciiBytes)
        {
            AddUnit(ref hash, NameFolding.Fold((char)value));
        }

        return hash;
    }

    static void AddUnit(ref ulong hash, char character)
    {
        hash = unchecked((hash ^ (byte)character) * Prime);
        hash = unchecked((hash ^ (byte)(character >> 8)) * Prime);
    }
}
