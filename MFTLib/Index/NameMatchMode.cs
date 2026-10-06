namespace MFTLib.Index;

/// <summary>How <see cref="SearchQuery.NamePattern" /> is compared with an entry's name.</summary>
public enum NameMatchMode
{
    /// <summary>The pattern is the entire name. <c>*</c> and <c>?</c> are ordinary characters.</summary>
    Exact,

    /// <summary>The pattern occurs anywhere in the name. <c>*</c> and <c>?</c> are ordinary characters.</summary>
    Substring,

    /// <summary>
    ///     The pattern must match the entire name, where <c>*</c> stands for any run of characters,
    ///     including none, and <c>?</c> for exactly one character.
    /// </summary>
    Glob,
}
