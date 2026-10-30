namespace MFTLib.Index;

/// <summary>
///     I1 Registry: live membership and view counts change under one lock. The registry lock precedes
///     scan state locks; it never encloses an await or gate acquisition.
///     I2 Closing: a successor waits for the previous scan's Closed task before taking canonical ownership.
/// </summary>
sealed class VolumeScanRegistry
{
    readonly Lock _gate = new();
    readonly Dictionary<VolumeScanKey, Registration> _live = [];
    readonly Dictionary<VolumeScanKey, Task> _closing = [];
    internal static VolumeScanRegistry Process { get; } = new();

    sealed class Registration(FileIndex.VolumeScan scan)
    {
        internal FileIndex.VolumeScan Scan { get; } = scan;
        internal int Views { get; set; } = 1;
    }

    internal FileIndex.VolumeScan Attach(VolumeScanKey key, Func<Task, FileIndex.VolumeScan> create)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(key, out var registered))
            {
                registered.Views++;
                return registered.Scan;
            }
            var scan = create(_closing.GetValueOrDefault(key, Task.CompletedTask));
            _live.Add(key, new Registration(scan));
            return scan;
        }
    }

    internal bool Detach(VolumeScanKey key, FileIndex.VolumeScan scan)
    {
        lock (_gate)
        {
            var registration = _live[key];
            if (!ReferenceEquals(registration.Scan, scan))
            {
                throw new InvalidOperationException("A view detached from a different registered scan.");
            }
            if (--registration.Views != 0)
            {
                return false;
            }
            _live.Remove(key);
            var closed = _closing.TryGetValue(key, out var previousClosed) && !previousClosed.IsCompleted
                ? Task.WhenAll(previousClosed, scan.Closed)
                : scan.Closed;
            _closing[key] = closed;
            _ = RemoveClosedAsync(key, closed);
            return true;
        }
    }

    async Task RemoveClosedAsync(VolumeScanKey key, Task closed)
    {
        await closed.ConfigureAwait(false);
        lock (_gate)
        {
            if (_closing.GetValueOrDefault(key) == closed)
            {
                _closing.Remove(key);
            }
        }
    }

    internal int LiveScanCount
    {
        get
        {
            lock (_gate)
            {
                return _live.Count;
            }
        }
    }

    internal bool IsLive(VolumeScanKey key)
    {
        lock (_gate)
        {
            return _live.ContainsKey(key);
        }
    }
}
