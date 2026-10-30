using System.Globalization;
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
        Func<FileIndexOptions, FileIndexOptions> prepareOptions,
        CancellationToken cancellationToken)
    {
        await using var ownedSession = session.ConfigureAwait(false);
        if (parsed.Verb is not WatchVerb.Scan)
        {
            session.Connecting += () => _output.WriteLine("Broker: connecting; a UAC prompt may follow.");
            session.Connected += () => _output.WriteLine("Broker: connected.");
        }

        var source = session.CreateIndexSource();
        var failureCount = await RunIndexAsync(session, source, parsed, prepareOptions, cancellationToken).ConfigureAwait(false);

        if (session.Ended.IsCompleted)
        {
            await _output.WriteLineAsync($"Broker ended: {await session.Ended.ConfigureAwait(false)}").ConfigureAwait(false);
        }

        if (failureCount > 0)
        {
            await _output.WriteLineAsync($"{failureCount} root operation(s) failed.").ConfigureAwait(false);
        }

        return failureCount > 0 ? 1 : 0;
    }

    // Opens one index per named root and runs the verb on them. Returns how many root operations failed; a root that
    // settles unusable is printed and counted, the others run. prepareOptions is the test seam for volume facts.
    async Task<int> RunIndexAsync(
        BrokerSession session,
        MftIndexSource source,
        WatchArguments parsed,
        Func<FileIndexOptions, FileIndexOptions> prepareOptions,
        CancellationToken cancellationToken)
    {
        if (parsed.Roots.Count == 0)
        {
            return 0;
        }

        IReadOnlyList<FileIndex> indices;
        try
        {
            indices = await OpenIndicesAsync(source, parsed, prepareOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _output.WriteLineAsync($"Error opening the index: {exception.Message}").ConfigureAwait(false);
            return 1;
        }

        var ownedIndices = indices.ToList();
        try
        {
            var indexSet = new FileIndexSet(indices);
            return parsed.Verb is WatchVerb.Watch
                ? await WatchIndexAsync(indexSet, parsed, ownedIndices,
                    (root, token) => FileIndex.OpenAsync(OptionsFor(source, parsed, root, new PhaseReporter(_output), prepareOptions), token),
                    cancellationToken).ConfigureAwait(false)
                : await RunEachRootAsync(session, indexSet.Indices, parsed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var index in ownedIndices)
            {
                await index.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    async Task<int> RunEachRootAsync(BrokerSession session, IReadOnlyList<FileIndex> indices, WatchArguments parsed, CancellationToken cancellationToken)
    {
        var failureCount = 0;
        foreach (var index in indices)
        {
            if (!await RunOnRootAsync(session, index, parsed, index.Status, cancellationToken).ConfigureAwait(false))
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
        Func<FileIndexOptions, FileIndexOptions> prepareOptions,
        CancellationToken cancellationToken)
    {
        var scanProgress = new PhaseReporter(_output);
        var openReporter = new RootOpenedReporter(_output, parsed.Roots.Count);
        var tasks = parsed.Roots.Select(root => OpenRootIndexAsync(
            root, OptionsFor(source, parsed, root, scanProgress, prepareOptions), openReporter, cancellationToken)).ToArray();
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

    FileIndexOptions OptionsFor(
        MftIndexSource source,
        WatchArguments parsed,
        RootKey root,
        IProgress<IndexScanProgress> scanProgress,
        Func<FileIndexOptions, FileIndexOptions> prepareOptions)
    {
        var cached = parsed.Verb is not WatchVerb.Scan;
        return prepareOptions(new FileIndexOptions
        {
            Root = root,
            Producer = parsed.Producer,
            CacheMode = cached ? CacheMode.Persistent : CacheMode.Disabled,
            CacheDirectory = cached ? parsed.CacheDirectory : null,
            CacheTag = cached ? WatchArguments.CacheTag : default,
            Diagnostics = cached ? _output.WriteLine : null,
            MftSource = source,
            ScanProgress = scanProgress
        });
    }

    static async Task<FileIndex> OpenRootIndexAsync(
        RootKey root,
        FileIndexOptions options,
        RootOpenedReporter openReporter,
        CancellationToken cancellationToken)
    {
        var index = await FileIndex.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        openReporter.Report(root);
        return index;
    }

    // A cached verb adds where the block came from, the watch state and any lost checkpoint to the status line.
    void ReportRoot(WatchArguments parsed, RootStatus status)
    {
        _output.WriteLine($"Volume serial: {status.VolumeSerial?.ToString("X8", CultureInfo.InvariantCulture) ?? "none"}");
        if (parsed.Verb is WatchVerb.Scan)
        {
            WriteStatus(status);
        }
        else
        {
            WriteRootDetail(status);
        }
    }

    // True when the drive ran to its end; a failure is printed and false.
    async Task<bool> RunOnRootAsync(
        BrokerSession session,
        FileIndex index,
        WatchArguments parsed,
        RootStatus status,
        CancellationToken cancellationToken)
    {
        var root = status.Root;
        await _output.WriteLineAsync($"=== Root {root}: ===").ConfigureAwait(false);
        var succeeded = true;
        try
        {
            ReportRoot(parsed, status);
            switch (parsed.Verb)
            {
                case WatchVerb.Rescan:
                    var result = await index.RescanAsync(cancellationToken).ConfigureAwait(false);
                    WriteResults("rescan", [result]);
                    succeeded = result.Outcome == RootOperationOutcome.Succeeded;
                    WriteRootDetail(index.Status);
                    break;
                case WatchVerb.Journal:
                    var volume = JournalVolumeOf(index, root, cancellationToken);
                    await WriteJournalAsync(session, index, volume, parsed, cancellationToken).ConfigureAwait(false);
                    break;
            }

            await _output.WriteLineAsync($"=== Root {root}: done ===").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            succeeded = false;
            await _output.WriteLineAsync($"Error on root {root}: {exception.Message}").ConfigureAwait(false);
        }

        await _output.WriteLineAsync(string.Empty).ConfigureAwait(false);
        return succeeded;
    }

    // The volume whose journal serves a root: the volume itself, or, for a directory root read from the MFT, the volume
    // holding the folder. Entry paths render under the real volume root (links already followed), so the longest
    // volume root that prefixes the root entry's path names it. A walked directory has no journal.
    VolumeKey JournalVolumeOf(FileIndex index, RootKey root, CancellationToken cancellationToken)
    {
        if (root.TryGetVolume(out var volume))
        {
            return volume;
        }

        if (index.Status.Producer == ProducerKind.Mft)
        {
            var path = index.Root(cancellationToken).Path;
            var holder = _volumes()
                .Where(candidate => path.StartsWith(candidate.RootDirectory, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.RootDirectory.Length)
                .FirstOrDefault();
            if (holder is not null)
            {
                return holder.Volume;
            }
        }

        throw new InvalidOperationException($"Root {root} is a directory with no USN journal.");
    }

    // Reports each drive as its OpenAsync completes, one self-describing line per drive. Reports can overlap or arrive out
    // of order when several drives open concurrently, so the counter and the write are serialized together.
    internal sealed class RootOpenedReporter(TextWriter output, int totalRootCount)
    {
        readonly Lock _gate = new();
        int _settledRootCount;

        public void Report(RootKey root)
        {
            lock (_gate)
            {
                _settledRootCount++;
                output.WriteLine($"{root}: opened ({_settledRootCount} of {totalRootCount} roots)");
            }
        }
    }
}
