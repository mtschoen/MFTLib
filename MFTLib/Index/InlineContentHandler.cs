namespace MFTLib.Index;

/// <summary>
///     Receives one <see cref="InlineContentResult" /> from <see cref="FileIndex.ReadInlineContentAsync" />.
///     Called serially, so it needs no locking of its own, and on a thread-pool thread. An exception it throws
///     stops the read and propagates unchanged from the returned task, even when a cancellation was also requested.
/// </summary>
/// <param name="result">The answer for one entry; valid only until the handler returns.</param>
public delegate void InlineContentHandler(InlineContentResult result);
