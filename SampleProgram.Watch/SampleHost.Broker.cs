using MFTLib;
using MFTLib.Index;

namespace SampleProgram.Watch;

// One FileIndex per named drive, opened through the broker, the path every consumer ships. scan opens CacheMode.Disabled and only
// reports; the other modes open cached indices and act on them. This process stays unelevated.
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

    // One broker, so one elevation prompt, serves every drive of the run, and one index per drive answers for its root. The exit code is 1 when no broker session could be
    // created, so nothing ran, or when any drive failed; otherwise 0.
    internal async Task<int> RunThroughBrokerAsync(
        WatchArguments parsed,
        BrokerSession session,
        Func<string, IndexedRoot> resolveDrive,
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

    // Resolves the drives, opens one index per resolved drive and runs the verb on them. Returns how many drive
    // operations failed; a drive that cannot be resolved or settles unusable is printed and counted, the others run.
    async Task<int> RunIndexAsync(
        BrokerSession session,
        MftIndexSource source,
        WatchArguments parsed,
        Func<string, IndexedRoot> resolveDrive,
        CancellationToken cancellationToken)
    {
        var failureCount = 0;
        var drives = new List<IndexedRoot>();
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

        IReadOnlyList<FileIndex> indices;
        try
        {
            indices = await OpenIndicesAsync(source, parsed, drives, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _output.WriteLineAsync($"Error opening the index: {exception.Message}").ConfigureAwait(false);
            return failureCount + 1;
        }

        try
        {
            var indexSet = new FileIndexSet(indices);
            return failureCount + (parsed.Verb is WatchVerb.Watch
                ? await WatchIndexAsync(indexSet, parsed, cancellationToken).ConfigureAwait(false)
                : await RunEachDriveAsync(session, indexSet.Indices, parsed, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            foreach (var index in indices)
            {
                await index.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    async Task<int> RunEachDriveAsync(BrokerSession session, IReadOnlyList<FileIndex> indices, WatchArguments parsed, CancellationToken cancellationToken)
    {
        var failureCount = 0;
        foreach (var index in indices)
        {
            if (!await RunOnDriveAsync(session, index, parsed, index.Status, cancellationToken).ConfigureAwait(false))
            {
                failureCount++;
            }
        }

        return failureCount;
    }

    // Opens one index per drive; each reports as it finishes opening as "n of m".
    async Task<IReadOnlyList<FileIndex>> OpenIndicesAsync(
        MftIndexSource source,
        WatchArguments parsed,
        List<IndexedRoot> drives,
        CancellationToken cancellationToken)
    {
        var scanProgress = new PhaseReporter(_output);
        var openReporter = new DriveOpenedReporter(_output, drives.Count);
        var tasks = drives.Select(drive => OpenDriveIndexAsync(
            source, parsed, drive, scanProgress, openReporter, cancellationToken)).ToArray();
        try
        {
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            foreach (var task in tasks)
            {
                if (task.IsCompletedSuccessfully)
                {
                    var opened = await task.ConfigureAwait(false);
                    await opened.DisposeAsync().ConfigureAwait(false);
                }
            }

            throw;
        }
    }

    async Task<FileIndex> OpenDriveIndexAsync(
        MftIndexSource source,
        WatchArguments parsed,
        IndexedRoot drive,
        IProgress<IndexScanProgress> scanProgress,
        DriveOpenedReporter openReporter,
        CancellationToken cancellationToken)
    {
        var cached = parsed.Verb is not WatchVerb.Scan;
        var options = new FileIndexOptions
        {
            Root = drive,
            CacheMode = cached ? CacheMode.Persistent : CacheMode.Disabled,
            CacheDirectory = cached ? parsed.CacheDirectory : null,
            CacheTag = cached ? WatchArguments.CacheTag : default,
            Diagnostics = cached ? _output.WriteLine : null,
            MftSource = source,
            RetainedFileNames = parsed.RetainedFileNames,
            ScanProgress = scanProgress
        };

        var index = await FileIndex.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        openReporter.Report(drive.Drive);
        return index;
    }

    // A cached verb adds where the block came from, the watch state and any lost checkpoint to the status line.
    void ReportDrive(WatchArguments parsed, RootStatus status)
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
        RootStatus status,
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
                    var result = await index.RescanAsync(cancellationToken).ConfigureAwait(false);
                    WriteResults("rescan", [result]);
                    succeeded = result.Outcome == DriveOperationOutcome.Succeeded;
                    WriteDriveDetail(index.Status);
                    break;
                case WatchVerb.Journal:
                    var volume = drive.TryGetVolume(out var journalDrive)
                        ? journalDrive
                        : throw new InvalidOperationException($"Drive {drive} is a directory root with no USN journal.");
                    await WriteJournalAsync(session, index, volume, parsed, cancellationToken).ConfigureAwait(false);
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

    // Reports each drive as its OpenAsync completes, one self-describing line per drive. Reports can overlap or arrive out
    // of order when several drives open concurrently, so the counter and the write are serialized together.
    internal sealed class DriveOpenedReporter(TextWriter output, int totalDriveCount)
    {
        readonly Lock _gate = new();
        int _settledDriveCount;

        public void Report(RootKey drive)
        {
            lock (_gate)
            {
                _settledDriveCount++;
                output.WriteLine($"{drive}: opened ({_settledDriveCount} of {totalDriveCount} drives)");
            }
        }
    }
}
