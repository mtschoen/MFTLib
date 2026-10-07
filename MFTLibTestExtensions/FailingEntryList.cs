using System.Collections;
using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>
///     A journal entry list that throws a scripted failure the moment the index's apply step reads
///     it, so the production pump classifies the failure as an apply fault.
/// </summary>
sealed class FailingEntryList(Exception failure) : IReadOnlyList<UsnJournalEntry>
{
    public int Count => throw failure;

    public UsnJournalEntry this[int index] => throw failure;

    public IEnumerator<UsnJournalEntry> GetEnumerator() => throw failure;

    IEnumerator IEnumerable.GetEnumerator() => throw failure;
}
