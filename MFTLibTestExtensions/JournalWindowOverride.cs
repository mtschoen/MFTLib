using MFTLib.Index;

namespace MFTLibTestExtensions;

internal sealed class JournalWindowOverride : IDisposable
{
    static readonly Lock OwnershipLock = new();
    static JournalWindowOverride? _active;
    readonly IDisposable _restore;
    bool _disposed;

    JournalWindowOverride(Func<char, SyntheticJournalWindow?> journal)
    {
        _restore = JournalCheckpointCheck.OverrideJournalForTest(drive =>
            journal(drive) is { } window
                ? new JournalWindow(window.JournalId, window.FirstUsn, window.NextUsn,
                    window.AllocationDelta, window.MaximumSize)
                : null);
    }

    internal static IDisposable Create(Func<char, SyntheticJournalWindow?> journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        lock (OwnershipLock)
        {
            if (_active is not null)
            {
                throw new InvalidOperationException(
                    "A synthetic journal-window override is already active. " +
                    "Use nonparallel fixtures and dispose the owning scope before acquiring another.");
            }

            var scope = new JournalWindowOverride(journal);
            _active = scope;
            return scope;
        }
    }

    public void Dispose()
    {
        lock (OwnershipLock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _restore.Dispose();
            }
            finally
            {
                _disposed = true;
                _active = null;
            }
        }
    }
}
