namespace MFTLib.Index;

/// <summary>The shape of a named block section's name, known on every platform so a request can be sized before a section exists.</summary>
static class BlockSectionNaming
{
    internal const string Prefix = "mftlib-block-";

    /// <summary>The length of every section name: the prefix, one drive letter, a dash and 32 hexadecimal digits.</summary>
    internal const int Length = 13 + 1 + 1 + 32;

    internal static bool IsValid(string name, VolumeKey drive)
    {
        drive = drive.Validated();
        if (name.Length != Length ||
            !name.StartsWith(Prefix, StringComparison.Ordinal) ||
            name[Prefix.Length] != drive.Letter ||
            name[Prefix.Length + 1] != '-')
        {
            return false;
        }

        foreach (var character in name.AsSpan(Prefix.Length + 2))
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
