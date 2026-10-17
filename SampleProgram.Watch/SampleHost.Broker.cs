using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Watch;

// Each drive opened as a FileIndex over the broker, the path every consumer ships. scan opens CacheMode.Disabled and only
// reports; the other modes open the cached index and act on it. This process stays unelevated.
partial class SampleHost
{
    static BrokerSession CreateBrokerSessionNative()
    {
        return OperatingSystem.IsWindows()
            ? new BrokerSession()
            : throw new PlatformNotSupportedException("The broker is Windows only.");
    }

    // One broker, so one elevation prompt, serves every drive of the run. The exit code is 1 when no broker session could be
    // created, so nothing ran, or when any drive failed; otherwise 0.
    internal async Task<int> RunThroughBrokerAsync(
        WatchArguments parsed,
        BrokerSession session,
        Func<string, IndexedDrive> resolveDrive,
        CancellationToken cancellationToken)
    {
        await using var ownedSession = session.ConfigureAwait(false);
        if (parsed.Verb is not WatchVerb.Scan)
        {
            session.Connecting += () => _output.WriteLine("Broker: connecting; a UAC prompt may follow.");
            session.Connected += () => _output.WriteLine("Broker: connected.");
        }

        var source = session.CreateIndexSource(parsed.ScanOptions);
        var failureCount = parsed.Verb is WatchVerb.Watch
            ? await WatchDrivesAsync(source, parsed, resolveDrive, cancellationToken).ConfigureAwait(false)
            : await RunEachDriveAsync(session, source, parsed, resolveDrive, cancellationToken).ConfigureAwait(false);

        if (session.Ended.IsCompleted)
        {
            _output.WriteLine($"Broker ended: {await session.Ended.ConfigureAwait(false)}");
        }

        if (failureCount > 0)
        {
            _output.WriteLine($"{failureCount} drive operation(s) failed.");
        }

        return failureCount > 0 ? 1 : 0;
    }

    async Task<int> RunEachDriveAsync(
        BrokerSession session,
        MftIndexSource source,
        WatchArguments parsed,
        Func<string, IndexedDrive> resolveDrive,
        CancellationToken cancellationToken)
    {
        var failureCount = 0;
        foreach (var letter in parsed.Drives)
        {
            if (!await RunOnDriveAsync(session, source, parsed, letter, resolveDrive, cancellationToken).ConfigureAwait(false))
            {
                failureCount++;
            }
        }

        return failureCount;
    }

    // Opens one drive's index and writes its status line; a failure after the open disposes the index again.
    async Task<FileIndex> OpenDriveAsync(
        MftIndexSource source,
        WatchArguments parsed,
        IndexedDrive drive,
        CancellationToken cancellationToken)
    {
        var cached = parsed.Verb is not WatchVerb.Scan;
        var options = new FileIndexOptions
        {
            Drives = [drive],
            CacheMode = cached ? CacheMode.Persistent : CacheMode.Disabled,
            CacheDirectory = cached && parsed.CacheDirectory is not null ? Path.Combine(parsed.CacheDirectory, parsed.PolicyDirectoryName) : null,
            CacheTag = cached ? WatchArguments.CacheTag : default,
            Diagnostics = cached ? _output.WriteLine : null,
            MftSource = source,
            ScanProgress = new PhaseReporter(_output),
            DriveOpenProgress = new DriveOpenedReporter(_output)
        };

        var index = await FileIndex.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            Action<DriveStatus> report = cached ? WriteDriveDetail : WriteStatus;
            report(index.Drives.Single());
            return index;
        }
        catch
        {
            await index.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // True when the drive ran to its end; a failure is printed and false.
    async Task<bool> RunOnDriveAsync(
        BrokerSession session,
        MftIndexSource source,
        WatchArguments parsed,
        string letter,
        Func<string, IndexedDrive> resolveDrive,
        CancellationToken cancellationToken)
    {
        _output.WriteLine($"=== Drive {letter}: ===");
        var succeeded = true;
        try
        {
            await using var index = await OpenDriveAsync(source, parsed, resolveDrive(letter), cancellationToken).ConfigureAwait(false);
            var drive = new DriveKey(letter[0]);
            switch (parsed.Verb)
            {
                case WatchVerb.Rescan:
                    var results = await index.RescanAsync([drive], cancellationToken).ConfigureAwait(false);
                    WriteResults("rescan", results);
                    succeeded = results.All(result => result.Outcome == DriveOperationOutcome.Succeeded);
                    WriteDriveDetail(index.Drives.Single());
                    break;
                case WatchVerb.Journal:
                    await WriteJournalAsync(session, index, drive, parsed, cancellationToken).ConfigureAwait(false);
                    break;
            }

            _output.WriteLine($"=== Drive {letter}: done ===");
        }
        catch (Exception exception)
        {
            succeeded = false;
            _output.WriteLine($"Error on drive {letter}: {exception.Message}");
        }

        _output.WriteLine(string.Empty);
        return succeeded;
    }

    // Reports each drive as it settles, one self-describing line per report. Reports arrive synchronously on the settling
    // threads and can overlap or arrive out of order, so the line carries its own count and the write is serialized.
    internal sealed class DriveOpenedReporter(TextWriter output) : IProgress<IndexDriveOpened>
    {
        readonly object _gate = new();

        public void Report(IndexDriveOpened value)
        {
            lock (_gate)
            {
                output.WriteLine($"{value.Drive}: opened ({value.SettledDriveCount} of {value.TotalDriveCount} drives)");
            }
        }
    }
}
