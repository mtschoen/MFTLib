namespace MFTLib.Index;

/// <summary>The shape of a named block section's name, known on every platform so a request can be sized before a section exists.</summary>
internal static class BlockSectionNaming
{
    internal const string Prefix = "mftlib-block-";

    /// <summary>The length of every section name: the prefix, one drive letter, a dash and 32 hexadecimal digits.</summary>
    internal const int Length = 13 + 1 + 1 + 32;
}
