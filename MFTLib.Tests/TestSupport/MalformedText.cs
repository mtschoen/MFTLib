namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Builds text with an unpaired surrogate at run time. A string literal in an attribute argument is stored as
///     UTF-8, which silently turns a lone surrogate into U+FFFD, so data rows pass the code unit instead.
/// </summary>
static class MalformedText
{
    /// <summary>Text that is "x" next to the lone surrogate <paramref name="codeUnit" />.</summary>
    /// <param name="codeUnit">A surrogate code unit, 0xD800 through 0xDFFF.</param>
    /// <param name="trailing">True to put the surrogate after the "x", false to put it before.</param>
    /// <returns>The malformed text.</returns>
    public static string Make(int codeUnit, bool trailing) =>
        trailing ? "x" + (char)codeUnit : (char)codeUnit + "x";
}
