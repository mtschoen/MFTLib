using System.Diagnostics;
using MFTLib;
using MFTLib.Index;

namespace TestProgram;

// The volume modes: each runs one public MftVolume operation against a drive and prints what it
// returned. They need an elevated process (raw volume access); the scan-drive mode is in
// DriveScanner.Broker.cs.
partial class DriveScanner
{
    const int EntriesShown = 20;

    internal Func<MftVolume, MftRecord[]> _readAllRecords = volume => volume.ReadAllRecords(resolvePaths: true);
    internal Func<MftVolume, UsnJournalCursor> _queryJournal = volume => volume.QueryUsnJournal();
    internal Func<MftVolume, UsnJournalSettings> _queryJournalSettings = volume => volume.QueryUsnJournalSettings();

    internal Func<MftVolume, UsnJournalCursor, (UsnJournalEntry[] Entries, UsnJournalCursor UpdatedCursor)> _readJournal =
        (volume, since) => volume.ReadUsnJournal(since);

    internal Func<MftVolume, UsnJournalCursor, CancellationToken,
        IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>> _watchJournal =
        (volume, since, cancellationToken) => volume.WatchUsnJournal(since, cancellationToken);

    internal Func<TimeSpan, CancellationTokenSource> _createWatchCancellation =
        duration => new CancellationTokenSource(duration);

    internal void ReadRecords(string drive)
    {
        RunOnVolume(drive, volume =>
        {
            var stopwatch = Stopwatch.StartNew();
            var records = _readAllRecords(volume);
            stopwatch.Stop();

            var directories = records.Count(record => record.IsDirectory);
            _writeLine($"Read {records.Length} records ({directories} directories) in {stopwatch.Elapsed}");
            foreach (var record in records.Take(EntriesShown))
            {
                _writeLine($"  {record.FullPath ?? record.FileName}");
            }
        });
    }

    internal void QueryJournal(string drive)
    {
        RunOnVolume(drive, volume =>
        {
            var cursor = _queryJournal(volume);
            var settings = _queryJournalSettings(volume);
            _writeLine($"Journal {cursor.JournalId}, next USN {cursor.NextUsn}");
            _writeLine($"  maximum size {settings.MaximumSize} bytes, allocation delta {settings.AllocationDelta} bytes");
        });
    }

    internal void ReadJournal(string drive)
    {
        RunOnVolume(drive, volume =>
        {
            var armed = _queryJournal(volume);
            _writeLine($"Armed journal {armed.JournalId} at USN {armed.NextUsn}");
            var records = _readAllRecords(volume);
            _writeLine($"Scanned {records.Length} records");

            var (entries, updated) = _readJournal(volume, armed);
            _writeLine($"Catch-up read {entries.Length} entries, cursor now {updated.NextUsn}");
            PrintEntries(entries);
        });
    }

    internal async Task WatchJournalAsync(string drive, int seconds)
    {
        var letter = drive.TrimEnd(':');
        _writeLine($"=== Drive {letter}: ===");
        try
        {
            using var volume = _openVolume(letter);
            var armed = _queryJournal(volume);
            _writeLine($"Watching journal {armed.JournalId} from USN {armed.NextUsn} for {seconds} seconds");
            using var cancellation = _createWatchCancellation(TimeSpan.FromSeconds(seconds));
            try
            {
                await foreach (var (entries, cursor) in _watchJournal(volume, armed, cancellation.Token))
                {
                    _writeLine($"Batch of {entries.Length} entries, cursor now {cursor.NextUsn}");
                    PrintEntries(entries);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                _writeLine("Watch ended.");
            }

            _writeLine($"=== Drive {letter}: done ===");
        }
        catch (Exception exception)
        {
            _writeLine($"Error on drive {letter}: {exception.Message}");
        }

        _writeLine(string.Empty);
    }

    void RunOnVolume(string drive, Action<MftVolume> operation)
    {
        var letter = drive.TrimEnd(':');
        _writeLine($"=== Drive {letter}: ===");
        try
        {
            using var volume = _openVolume(letter);
            operation(volume);
            _writeLine($"=== Drive {letter}: done ===");
        }
        catch (Exception exception)
        {
            _writeLine($"Error on drive {letter}: {exception.Message}");
        }

        _writeLine(string.Empty);
    }

    void PrintEntries(UsnJournalEntry[] entries)
    {
        foreach (var entry in entries.Take(EntriesShown))
        {
            _writeLine($"  USN {entry.Usn} {entry.Reason} {entry.FileName}");
        }
    }
}
