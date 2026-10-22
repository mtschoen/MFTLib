namespace MFTLibTestExtensions;

/// <summary>
///     A process-global seam override that only one holder may own at a time per seam description.
///     Disposal restores the seam and is idempotent.
/// </summary>
sealed class ProcessGlobalScope : IDisposable
{
    static readonly Lock OwnershipLock = new();
    static readonly HashSet<string> ActiveSeams = [];
    readonly string _description;
    readonly Action _restore;
    bool _disposed;

    ProcessGlobalScope(string description, Action restore)
    {
        _description = description;
        _restore = restore;
    }

    /// <summary>Installs the seam and returns the scope that restores it; <paramref name="install" /> returns the restore action.</summary>
    internal static IDisposable Acquire(string description, Func<Action> install)
    {
        lock (OwnershipLock)
        {
            if (ActiveSeams.Contains(description))
            {
                throw new InvalidOperationException(
                    $"{description} is already active. " +
                    "Use nonparallel fixtures and dispose the owning scope before acquiring another.");
            }

            var scope = new ProcessGlobalScope(description, install());
            ActiveSeams.Add(description);
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
                _restore();
            }
            finally
            {
                _disposed = true;
                ActiveSeams.Remove(_description);
            }
        }
    }
}
