namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Faults a rescan took over from a session it reclaimed, other than the rescanned drive's,
    ///     that no <see cref="StopWatchingAsync" /> has reported yet. They belong to no running
    ///     session, so a session that later ends cleanly and releases itself cannot drop them, and
    ///     the next stop reports the earliest of them ahead of any fault its own session raised,
    ///     then clears them. A later rescan that recovers one of these drives removes its entry.
    ///     Read and written under <see cref="_stateLock" />.
    /// </summary>
    readonly WatchSessionFaults _unreportedWatchFaults = new();

    /// <summary>
    ///     Outstanding pump faults for one session, or, as <see cref="_unreportedWatchFaults" />,
    ///     the faults rescans retained from reclaimed sessions. Callers hold the index's state lock.
    ///     Drive recovery removes only its normalized letter slot; session faults survive it.
    /// </summary>
    sealed class WatchSessionFaults
    {
        const int SubscriberSlot = -1;
        const int SourceSlot = -2;
        readonly Dictionary<int, Entry> _entries = [];
        long _nextSequence;

        public sealed record Entry(long Sequence, Exception Exception);

        public bool HasFaults => _entries.Count != 0;

        public Exception? FirstOutstanding => _entries.Values
            .OrderBy(entry => entry.Sequence).FirstOrDefault()?.Exception;

        /// <summary>
        ///     Takes on every fault still outstanding in <paramref name="previous" /> except the slot
        ///     of <paramref name="recoveredDriveLetter" />, in the order they were raised and after
        ///     everything already held, so the earliest fault stays the one a stop rethrows. A slot
        ///     already held keeps its older fault.
        /// </summary>
        public void RetainFrom(WatchSessionFaults previous, char recoveredDriveLetter)
        {
            var recoveredSlot = (int)char.ToUpperInvariant(recoveredDriveLetter);
            foreach (var (slot, entry) in previous._entries.OrderBy(pair => pair.Value.Sequence))
            {
                if (slot != recoveredSlot)
                {
                    Record(slot, entry.Exception);
                }
            }
        }

        public void Clear() => _entries.Clear();

        public void RecordDrive(char driveLetter, Exception exception) =>
            Record(char.ToUpperInvariant(driveLetter), exception);

        /// <summary>
        ///     Returns true only for the session's first subscriber fault. The pump announces
        ///     <c>WatchFaulted</c> on that signal alone, so a handler that throws on every batch is
        ///     reported once. Drive and source recording need no such signal: a drive is recorded only
        ///     on its first drop, and a source failure ends the pump, so it is recorded once.
        /// </summary>
        public bool RecordSubscriber(Exception exception) => Record(SubscriberSlot, exception);

        public void RecordSource(Exception exception) => Record(SourceSlot, exception);

        public Entry? TakeDrive(char driveLetter)
        {
            _entries.Remove(char.ToUpperInvariant(driveLetter), out var entry);
            return entry;
        }

        public void RestoreDrive(char driveLetter, Entry? entry)
        {
            if (entry is not null)
            {
                _entries.TryAdd(char.ToUpperInvariant(driveLetter), entry);
            }
        }

        bool Record(int slot, Exception exception)
        {
            if (_entries.ContainsKey(slot))
            {
                return false;
            }

            _entries.Add(slot, new Entry(_nextSequence++, exception));
            return true;
        }
    }
}
