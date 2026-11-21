namespace MFTLib.Index;

/// <summary>
///     The answer for one entry, lent to an <see cref="InlineContentHandler" /> for the duration of the call.
///     A ref struct: it cannot be stored, captured or held across an <c>await</c>, so copy
///     <see cref="Content" /> out if it must outlive the call.
/// </summary>
public readonly ref struct InlineContentResult
{
    internal InlineContentResult(int index, InlineContentOutcome outcome, ReadOnlySpan<byte> content)
    {
        Index = index;
        Outcome = outcome;
        Content = content;
    }

    /// <summary>The position of the entry in the list passed to <see cref="FileIndex.ReadInlineContentAsync" />.</summary>
    public int Index { get; }

    /// <summary>What was found for the entry.</summary>
    public InlineContentOutcome Outcome { get; }

    /// <summary>
    ///     The file's resident <c>$DATA</c> bytes. Valid only during the handler call, and empty unless
    ///     <see cref="Outcome" /> is <see cref="InlineContentOutcome.Returned" />, where an empty span is a valid
    ///     empty file.
    /// </summary>
    public ReadOnlySpan<byte> Content { get; }
}
