using MFTLib.Index;

namespace MFTLib.Tests.Index;

/// <summary>
///     Wraps a <see cref="SyntheticBlockBuilder.MftShaped" /> block in a
///     <see cref="Snapshot" />/<see cref="BlockWriter" /> pair so a test can apply journal
///     entries directly through a <see cref="JournalMutator" /> without repeating the setup.
/// </summary>
internal sealed class MutatorFixture : IDisposable
{
    readonly SyntheticBlockBuilder _builder;
    readonly Snapshot _snapshot;
    readonly JournalMutator _mutator;
    long _nextUsn = 1000;

    public MutatorFixture()
    {
        _builder = SyntheticBlockBuilder.MftShaped();
        var block = _builder.OpenForWriting();
        var driveBlock = new DriveBlock(_builder.DriveLetter, 0, block);
        _snapshot = Snapshot.Create([driveBlock]);
        _mutator = new JournalMutator(new BlockWriter(block));
    }

    public DateTime Timestamp { get; } = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);
    public BlockFile Block => _mutator.Writer.Block;

    public IReadOnlyList<FileChange> Apply(IReadOnlyList<UsnJournalEntry> entries)
    {
        return _mutator.Apply(_snapshot, driveOrdinal: 0, entries, journalId: 7, _nextUsn++);
    }

    public void Dispose()
    {
        _snapshot.ReleaseNow();
        _builder.Dispose();
    }
}
