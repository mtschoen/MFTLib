using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Watch;

// One FileIndex over every named drive, opened through the broker, the path every consumer ships. scan opens CacheMode.Disabled and only
// reports; the other modes open the cached index and act on it. This process stays unelevated.
partial class SampleHost
{
    static BrokerSession CreateBrokerSessionNative(int? connectTimeoutSeconds)
    {
        return OperatingSystem.IsWindows()
            ? connectTimeoutSeconds is { } seconds
                ? new BrokerSession(BrokerLauncher.Launch, TimeSpan.FromSeconds(seconds))
                : new BrokerSession()
            : throw new PlatformNotSupportedException("The broker is Windows only.");
    }

    // One broker, so one elevation prompt, serves every drive of the run, and one index answers for all of them. The exit code is 1 when no broker session could be
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

        var source = session.CreateIndexSource();
        var failureCount = await RunIndexAsync(session, source, parsed, resolveDrive, cancellationToken).ConfigureAwait(false);

        if (session.Ended.IsCompleted)
        {
            await _output.WriteLineAsync($"Broker ended: {await session.Ended.ConfigureAwait(false)}").ConfigureAwait(false);
        }

        if (failureCount > 0)
        {
            await _output.WriteLineAsync($"{failureCount} drive operation(s) failed.").ConfigureAwait(false);
        }

        return failureCount > 0 ? 1 : 0;
    }

    // Resolves the drives, opens the one index over those that resolved and runs the verb on it. Returns how many drive
    // operations failed; a drive that cannot be resolved or settles unusable is printed and counted, the others run.
    async Task<int> RunIndexAsync(
        BrokerSession session,
        MftIndexSource source,
        WatchArguments parsed,
        Func<string, IndexedDrive> resolveDrive,
        CancellationToken cancellationToken)
    {
        var failureCount = 0;
        var drives = new List<IndexedDrive>();
        foreach (var letter in parsed.Drives)
        {
            try
            {
                drives.Add(resolveDrive(letter));
            }
            catch (Exception exception)
            {
                failureCount++;
                await _output.WriteLineAsync($"Error on drive {letter}: {exception.Message}").ConfigureAwait(false);
            }
        }

        if (drives.Count == 0)
        {
            return failureCount;
        }

        FileIndex index;
        try
        {
            index = await OpenIndexAsync(source, parsed, drives, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _output.WriteLineAsync($"Error opening the index: {exception.Message}").ConfigureAwait(false);
            return failureCount + 1;
        }

        await using var ownedIndex = index.ConfigureAwait(false);
        return failureCount + (parsed.Verb is WatchVerb.Watch
            ? await WatchIndexAsync(index, parsed, cancellationToken).ConfigureAwait(false)
            : await RunEachDriveAsync(session, index, parsed, cancellationToken).ConfigureAwait(false));
    }

    async Task<int> RunEachDriveAsync(BrokerSession session, FileIndex index, WatchArguments parsed, CancellationToken cancellationToken)
    {
        var failureCount = 0;
        foreach (var status in index.Drives)
        {
            if (!await RunOnDriveAsync(session, index, parsed, status, cancellationToken).ConfigureAwait(false))
            {
                failureCount++;
            }
        }

        return failureCount;
    }

    // Opens one index over every drive; its open progress reports each drive settling as "n of m".
    Task<FileIndex> OpenIndexAsync(
        MftIndexSource source,
        WatchArguments parsed,
        IReadOnlyList<IndexedDrive> drives,
        CancellationToken cancellationToken)
    {
        var cached = parsed.Verb is not WatchVerb.Scan;
        var options = new FileIndexOptions
        {
            Drives = drives,
            CacheMode = cached ? CacheMode.Persistent : CacheMode.Disabled,
            CacheDirectory = cached ? parsed.CacheDirectory : null,
            CacheTag = cached ? WatchArguments.CacheTag : default,
            Diagnostics = cached ? _output.WriteLine : null,
            MftSource = source,
            RetainedFileNames = parsed.RetainedFileNames,
            ScanProgress = new PhaseReporter(_output),
            DriveOpenProgress = new DriveOpenedReporter(_output)
        };

        return FileIndex.OpenAsync(options, cancellationToken);
    }

    // A cached verb adds where the block came from, the watch state and any lost checkpoint to the status line.
    void ReportDrive(WatchArguments parsed, DriveStatus status)
    {
        if (parsed.Verb is WatchVerb.Scan)
        {
            WriteStatus(status);
        }
        else
        {
            WriteDriveDetail(status);
        }
    }

    // True when the drive ran to its end; a failure is printed and false.
    async Task<bool> RunOnDriveAsync(
        BrokerSession session,
        FileIndex index,
        WatchArguments parsed,
        DriveStatus status,
        CancellationToken cancellationToken)
    {
        var drive = status.Drive;
        await _output.WriteLineAsync($"=== Drive {drive}: ===").ConfigureAwait(false);
        var succeeded = true;
        try
        {
            ReportDrive(parsed, status);
            switch (parsed.Verb)
            {
                case WatchVerb.Rescan:
                    var results = await index.RescanAsync([drive], cancellationToken).ConfigureAwait(false);
                    WriteResults("rescan", results);
                    succeeded = results.All(result => result.Outcome == DriveOperationOutcome.Succeeded);
                    WriteDriveDetail(index.Drives.Single(candidate => candidate.Drive == drive));
                    break;
                case WatchVerb.Journal:
                    await WriteJournalAsync(session, index, drive, parsed, cancellationToken).ConfigureAwait(false);
                    break;
            }

            await _output.WriteLineAsync($"=== Drive {drive}: done ===").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            succeeded = false;
            await _output.WriteLineAsync($"Error on drive {drive}: {exception.Message}").ConfigureAwait(false);
        }

        await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
        return succeeded;
    }

    // Reports each drive as it settles, one self-describing line per report. Reports arrive synchronously on the settling
    // threads and can overlap or arrive out of order, so the line carries its own count and the write is serialized.
    internal sealed class DriveOpenedReporter(TextWriter output) : IProgress<IndexDriveOpened>
    {
        readonly Lock _gate = new();

        public void Report(IndexDriveOpened value)
        {
            lock (_gate)
            {
                output.WriteLine($"{value.Drive}: opened ({value.SettledDriveCount} of {value.TotalDriveCount} drives)");
            }
        }
    }
}
