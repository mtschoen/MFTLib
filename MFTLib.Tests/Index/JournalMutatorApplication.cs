using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The mutator tests' way to apply a batch the block has room for: the whole batch through
///     <see cref="JournalMutator.ApplyUntilRefusal" />, failing the test on any refusal. A test about a refusal calls
///     <see cref="JournalMutator.ApplyUntilRefusal" /> itself, and growth and skipping are the index's
///     (<c>FileIndexWatchGrowthTests</c>).
/// </summary>
static class JournalMutatorApplication
{
    public static IReadOnlyList<FileChange> ApplyWhole(this JournalMutator mutator, RootBlock rootBlock,
        IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn)
    {
        var result = mutator.ApplyUntilRefusal(rootBlock, entries, 0, journalIdentifier, nextUsn);
        Assert.IsNull(result.Refusal, "the test block was expected to have room for the whole batch");
        return result.Changes;
    }
}
