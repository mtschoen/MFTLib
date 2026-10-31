using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Shared fixtures for the posting query tests: a seeded synthetic corpus, journal-style delta appends fabricated
///     directly in the regions, and a row-scan reference that is the test oracle (never a production fallback).
/// </summary>
static class PostingQueryTestSupport
{
    internal static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    internal const uint DirectoryCount = 20;

    /// <summary>
    ///     Names that exercise the fold: casings of one spelling, non-ASCII folds, an unpaired surrogate and a name that
    ///     most rows share.
    /// </summary>
    internal static readonly string[] SpecialNames =
    [
        "index.js", "Index.JS", "INDEX.js", "café.txt", "CAFÉ.TXT", "ındex.js", "ſetup.py", "setup.py",
        "\uD800lone.bin", "Makefile", "makefile", "README.md", "readme.MD"
    ];

    internal sealed class Corpus(uint rowCount) : IDisposable
    {
        internal SyntheticBlockBuilder Builder { get; } = new(slotCapacity: rowCount + 64, namePoolCapacity: 1 << 20);

        internal List<string> Names { get; } = [.. SpecialNames];

        internal uint RowCount { get; } = rowCount;

        public void Dispose() => Builder.Dispose();
    }

    /// <summary>A separate read mapping of a builder's block, as a query sees it, released with its root block.</summary>
    internal sealed class OpenedRoot : IDisposable
    {
        internal OpenedRoot(SyntheticBlockBuilder builder)
        {
            Root = new RootBlock(new VolumeKey('T'),
                builder.OpenForReading(out _) ?? throw new InvalidOperationException("The block did not open."));
        }

        internal RootBlock Root { get; }

        internal BlockFile Block => Root.Block;

        public void Dispose() => Root.ReleaseNowAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    ///     A complete block of directories (rows 1 to <see cref="DirectoryCount" />) and files with seeded mixed-case names,
    ///     enough distinct names that the 1,024 buckets chain, a few tombstones and varied sizes and times. Row 0 is the
    ///     unnamed root.
    /// </summary>
    internal static Corpus BuildCorpus(int seed, uint rowCount = 3000)
    {
        var random = new Random(seed);
        var corpus = new Corpus(rowCount);
        var builder = corpus.Builder;
        var names = corpus.Names;
        for (var index = 0; index < 700; index++)
        {
            var name = $"file{random.Next(0, 5000)}.dat";
            names.Add(random.Next(3) == 0 ? name.ToUpperInvariant() : name);
        }

        builder.AddRoot();
        for (var directory = 1u; directory <= DirectoryCount; directory++)
        {
            builder.AddRow($"dir{directory}", 0, RowFlags.InUse | RowFlags.Directory, 0, Moment, 0);
        }

        for (var row = DirectoryCount + 1; row < rowCount; row++)
        {
            var name = names[random.Next(names.Count)];
            var flags = random.Next(25) == 0 ? RowFlags.InUse | RowFlags.Tombstone : RowFlags.InUse;
            if (random.Next(40) == 0)
            {
                flags |= RowFlags.Directory;
            }

            builder.AddRow(name, (uint)random.Next(1, (int)DirectoryCount + 1), flags, random.Next(0, 10_000),
                Moment.AddDays(random.Next(0, 100)), 0);
        }

        builder.Complete(Moment);
        return corpus;
    }

    /// <summary>
    ///     Changes the names of about <paramref name="changes" /> seeded rows after the base was built: renames with a journal-style delta
    ///     (which leaves the old association stale), renames away and back (a duplicate association), and renames to names
    ///     interned after the build. New spellings are added to the corpus so queries cover them.
    /// </summary>
    internal static void Churn(Corpus corpus, int seed, int changes)
    {
        var random = new Random(seed);
        var builder = corpus.Builder;
        var block = builder.OpenForWriting();
        for (var change = 0; change < changes; change++)
        {
            var row = (uint)random.Next((int)DirectoryCount + 1, (int)corpus.RowCount);
            var original = NameIdOf(block, row);
            var other = NameIdOf(block, (uint)random.Next((int)DirectoryCount + 1, (int)corpus.RowCount));
            switch (random.Next(3))
            {
                case 0:
                    // The old association stays behind, stale.
                    RenameWithDelta(builder, row, other);
                    break;
                case 1:
                    RenameWithDelta(builder, row, other);
                    RenameWithDelta(builder, row, original);
                    break;
                default:
                    var name = $"Interned-Later-{random.Next(0, 40)}.Dat";
                    corpus.Names.Add(name);
                    Assert.IsTrue(NamePool.TryGetOrAdd(block, name, out var interned));
                    RenameWithDelta(builder, row, interned);
                    break;
            }
        }
    }

    /// <summary>
    ///     Appends one association the way the journal does: entry first, then the published count, then the head, in
    ///     the order <c>docs/index-format.md</c> gives for readers.
    /// </summary>
    internal static void AppendDelta(BlockFile block, uint row, uint nameId)
    {
        var count = block.DeltaEntryCount + 1;
        var head = (int)(nameId & (block.BucketCount - 1));
        block.DeltaEntries[(int)count] = new DeltaEntry { Row = row, NameId = nameId, Next = block.DeltaHeads[head] };
        block.PublishDeltaEntryCount(count);
        Volatile.Write(ref block.DeltaHeads[head], count);
    }

    internal static uint NameIdOf(BlockFile block, uint row) =>
        FileRow.DescriptorNameId(FileRow.ReadDescriptorWord(in block.Rows[(int)row]));

    /// <summary>A rename the journal would write: the descriptor changes and the new name is associated with the row.</summary>
    internal static void RenameWithDelta(SyntheticBlockBuilder builder, uint row, uint nameId)
    {
        var block = builder.OpenForWriting();
        AppendDelta(block, row, nameId);
        builder.MutateNameDescriptor(row, nameId);
    }

    /// <summary>The rows an exact query accepts, by the old row pass: every row tested against the name pass and predicates.</summary>
    internal static uint[] ReferenceRows(RootBlock root, SearchQuery query, CancellationToken cancellationToken)
    {
        var subtree = SubtreeAnchor.Resolve(root, query);
        using var membership = SubtreeAnchor.CreateMembership(root, subtree, null);
        using var names = new NameQueryMatches(root.Block, query, cancellationToken);
        var rows = root.Block.Rows;
        var matches = new List<uint>();
        for (var row = 0u; row < root.Block.RowCount; row++)
        {
            if (SearchEngine.RowMatches(in rows[(int)row], names, query) &&
                (membership is null || membership.Contains(row)))
            {
                matches.Add(row);
            }
        }

        return [.. matches];
    }
}
