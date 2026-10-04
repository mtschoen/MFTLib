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

    internal Func<MftVolume, bool, bool, (MftRecord[] Records, MftParseTimings? Timings)> _readAllRecords =
        ReadAllRecordsNative;

    internal Func<MftVolume, UsnJournalCursor> _queryJournal = volume => volume.QueryUsnJournal();
    internal Func<MftVolume, UsnJournalSettings> _queryJournalSettings = volume => volume.QueryUsnJournalSettings();

    internal Func<MftVolume, UsnJournalCursor, (UsnJournalEntry[] Entries, UsnJournalCursor UpdatedCursor)> _readJournal =
        (volume, since) => volume.ReadUsnJournal(since);

    internal Func<MftVolume, UsnJournalCursor, CancellationToken,
        IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>> _watchJournal =
        (volume, since, cancellationToken) => volume.WatchUsnJournal(since, cancellationToken);

    internal Func<TimeSpan, CancellationTokenSource> _createTimedCancellation =
        duration => new CancellationTokenSource(duration);

    // Each combination of the two options is a different ReadAllRecords overload.
    static (MftRecord[] Records, MftParseTimings? Timings) ReadAllRecordsNative(MftVolume volume,
        bool resolvePaths, bool withTimings)
    {
        if (!withTimings)
        {
            return (resolvePaths ? volume.ReadAllRecords(true) : volume.ReadAllRecords(), null);
        }

        MftParseTimings timings;
        var records = resolvePaths ? volume.ReadAllRecords(true, out timings) : volume.ReadAllRecords(out timings);
        return (records, timings);
    }

    internal void ReadRecords(string drive, ModeOptions options)
    {
        RunOnVolume(drive, options, volume =>
        {
            var stopwatch = Stopwatch.StartNew();
            var (records, timings) = _readAllRecords(volume, !options.NoPaths, options.Timings);
            stopwatch.Stop();

            var directories = records.Count(record => record.IsDirectory);
            _writeLine($"Read {records.Length} records ({directories} directories) in {stopwatch.Elapsed}");
            if (timings is { } parseTimings)
            {
                _writeLine($"  {parseTimings}");
            }

            PrintRecords(records);
        });
    }

    // The name lookup without timings: find-git is the same call with them.
    internal void FindName(string drive, ModeOptions options)
    {
        RunOnVolume(drive, options, volume =>
        {
            var stopwatch = Stopwatch.StartNew();
            var records = volume.FindByName(options.RequiredName, options.ToMatchFlags());
            stopwatch.Stop();

            _writeLine($"Found {records.Length} records matching {options.Name} in {stopwatch.Elapsed}");
            PrintRecords(records);
        });
    }

    internal void QueryJournal(string drive)
    {
        RunOnVolume(drive, new ModeOptions(), volume =>
        {
            var cursor = _queryJournal(volume);
            var settings = _queryJournalSettings(volume);
            _writeLine($"Journal {cursor.JournalId}, next USN {cursor.NextUsn}");
            _writeLine($"  {FormatSettings(settings)}");
        });
    }

    internal void ReadJournal(string drive, ModeOptions options)
    {
        RunOnVolume(drive, options, volume =>
        {
            var armed = _queryJournal(volume);
            _writeLine($"Armed journal {armed.JournalId} at USN {armed.NextUsn}");
            var (records, _) = _readAllRecords(volume, true, false);
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
            using var cancellation = _createTimedCancellation(TimeSpan.FromSeconds(seconds));
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

    MftVolume OpenVolume(string letter, ModeOptions options)
    {
        return options.BufferSizeRecords is { } bufferSizeRecords
            ? _openVolumeWithBuffer(letter, bufferSizeRecords)
            : _openVolume(letter);
    }

    void RunOnVolume(string drive, ModeOptions options, Action<MftVolume> operation)
    {
        var letter = drive.TrimEnd(':');
        _writeLine($"=== Drive {letter}: ===");
        try
        {
            using var volume = OpenVolume(letter, options);
            operation(volume);
            _writeLine($"=== Drive {letter}: done ===");
        }
        catch (Exception exception)
        {
            _writeLine($"Error on drive {letter}: {exception.Message}");
        }

        _writeLine(string.Empty);
    }
}
