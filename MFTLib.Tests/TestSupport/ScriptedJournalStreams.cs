using System.Runtime.CompilerServices;
using MFTLibTestExtensions;

namespace MFTLib.Tests.TestSupport;

static class ScriptedJournalStreams
{
    public static async IAsyncEnumerable<(
        IReadOnlyList<SyntheticJournalRecord> Entries, SyntheticJournalCursor Cursor)> OneBatchThenQuiet(
        SyntheticJournalRecord[] entries, SyntheticJournalCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return (entries, cursor);
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
}
