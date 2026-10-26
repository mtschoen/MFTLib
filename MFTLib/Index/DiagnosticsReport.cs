namespace MFTLib.Index;

/// <summary>
///     The one place a <see cref="FileIndexOptions.Diagnostics" /> callback is invoked. The callback is
///     the consumer's: its failure must never replace the operation's own result or exception, escape a
///     release or <see cref="FileIndex.DisposeAsync" />, or skip the cleanup that follows a report.
/// </summary>
internal static class DiagnosticsReport
{
    /// <summary>
    ///     Invokes <paramref name="sink" /> with <paramref name="line" /> and contains every exception
    ///     the callback throws except <see cref="OutOfMemoryException" />. A null sink logs nothing.
    /// </summary>
    internal static void Send(Action<string>? sink, string line)
    {
        try
        {
            sink?.Invoke(line);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Swallowed by design: see the summary.
        }
    }
}
