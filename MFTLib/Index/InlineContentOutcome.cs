namespace MFTLib.Index;

/// <summary>
///     What <see cref="FileIndex.ReadInlineContentAsync" /> found for one entry. Only <see cref="Returned" /> carries
///     content; every other value is a reason the consumer reads the file another way or skips it.
/// </summary>
public enum InlineContentOutcome
{
    /// <summary>
    ///     The complete unnamed resident <c>$DATA</c> of the file's base record, including valid empty content.
    /// </summary>
    Returned,

    /// <summary>
    ///     The content is not in the base record: nonresident data, data described through an attribute list or held
    ///     in an extension record, a directory, no unnamed <c>$DATA</c> or more than one, a compressed, encrypted or
    ///     sparse data attribute, or a reparse point (a reparse attribute or the reparse file attribute). The consumer
    ///     falls back to <see cref="FileEntry.Open" />.
    /// </summary>
    NotInline,

    /// <summary>
    ///     The entry is deleted or superseded in the index, or the record on disk is free, past the end of the MFT,
    ///     or carries a different sequence number than the entry.
    /// </summary>
    Superseded,

    /// <summary>
    ///     The record failed its fixup or structure checks, its own read failed or came up short, or it does not lie
    ///     inside one run of the <c>$MFT</c> and so cannot be read as one piece.
    /// </summary>
    Unreadable
}
