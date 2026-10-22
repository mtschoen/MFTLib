using MFTLib.Index;

namespace MFTLib.Tests.Index;

/// <summary>
///     Wraps a <see cref="SyntheticBlockBuilder.MftShaped" /> block in a
///     <see cref="DriveBlock" />/<see cref="BlockWriter" /> pair so a test can apply journal
///     entries directly through a <see cref="JournalMutator" /> without repeating the setup.
/// </summary>
sealed class MutatorFixture : IAsyncDisposable
{
    readonly SyntheticBlockBuilder _builder;
    readonly DriveBlock[] _snapshot;
    readonly JournalMutator _mutator;
    long _nextUsn = 1000;

    public MutatorFixture()
    {
        _builder = SyntheticBlockBuilder.MftShaped();
        var block = _builder.OpenForWriting();
        var driveBlock = new DriveBlock(_builder.Drive, 0, block,
            rootDirectoryPath: TestDriveRoot.For(_builder.Drive));
        _snapshot = [driveBlock];
        _mutator = new JournalMutator(new BlockWriter(block));
    }

    public DateTime Timestamp { get; } = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);
    public BlockFile Block => _mutator.Writer.Block;
    public DriveBlock[] Snapshot => _snapshot;

    public IReadOnlyList<FileChange> Apply(IReadOnlyList<UsnJournalEntry> entries)
    {
        return _mutator.Apply(_snapshot[0], entries, journalIdentifier: 7, _nextUsn++);
    }

    public async ValueTask DisposeAsync()
    {
        await _snapshot.ReleaseNowAsync();
        _builder.Dispose();
    }
}
