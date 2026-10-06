using MFTLib.Index;

namespace TestProgram;

// The lifecycle verbs: rescan, watch and journal. rescan and watch take one scope that selects among the
// single-drive, drive-list and all-drives overloads of each lifecycle call.
partial class DriveScanner
{
    // How watch waits out its observation window; a test replaces it so nothing sleeps.
    internal Func<TimeSpan, CancellationToken, Task> _delay = Task.Delay;

    // Applies one lifecycle call at the scope the command line chose and prints each drive's result.
    async Task ApplyAsync(string call, IndexVerbArguments verb, Func<char, Task> single,
        Func<IReadOnlyList<char>, Task<IReadOnlyList<DriveOperationResult>>> list,
        Func<Task<IReadOnlyList<DriveOperationResult>>> all)
    {
        try
        {
            if (verb.Text("--drive-scope") is { Length: 1 } letter)
            {
                await single(char.ToUpperInvariant(letter[0])).ConfigureAwait(false);
                _writeLine($"  {call} {letter}: done");
                return;
            }

            var results = verb.Text("--drive-list") is { } letters
                ? await list(letters.Split(',').Select(text => char.ToUpperInvariant(text[0])).ToArray()).ConfigureAwait(false)
                : await all().ConfigureAwait(false);
            results.ToList().ForEach(result => _writeLine($"  {call} {result.DriveLetter}: {result.Outcome} {result.Failure?.Message}"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _writeLine($"  {call} failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    async Task RescanAsync(FileIndex index, IndexVerbArguments verb)
    {
        var token = CancellationToken.None;
        await ApplyAsync("rescan", verb, letter => index.RescanAsync(letter, token), letters => index.RescanAsync(letters, token),
            () => index.RescanAsync(token)).ConfigureAwait(false);
        index.Drives.ToList().ForEach(status => _writeLine(Describe(status)));
    }

    async Task WatchAsync(OpenedIndex opened, IndexVerbArguments verb)
    {
        var index = opened.Index;
        var token = CancellationToken.None;
        index.Changed += change => _writeLine($"  {change.Kind} {change.Path} (was {change.PreviousPath}) at {change.Timestamp:u}: {Describe(change.Entry)}");
        index.WatchStateChanged += state => _writeLine($"  state {state.DriveLetter}: {state.State} v{state.Version} fault {state.Fault?.Kind}");
        index.WatchFaulted += fault => _writeLine(
            $"  fault {fault.Kind} on {fault.DriveLetter}: {fault.Exception.Message}" +
            (fault.Exception is JournalCatchUpLostException lost
                ? $" (recovery stopped {lost.RecoveryStopped}; automatic recovery gives up after {FileIndex.LostCatchUpRecoveryLimit} lost catch-ups)"
                : string.Empty));

        await ApplyAsync("start", verb, letter => index.StartWatchingAsync(letter, token),
            letters => index.StartWatchingAsync(letters, token), () => index.StartWatchingAsync(token)).ConfigureAwait(false);
        await ApplyAsync("catch-up", verb, letter => index.WaitForCatchUpAsync(letter, token),
            letters => index.WaitForCatchUpAsync(letters, token), () => index.WaitForCatchUpAsync(token)).ConfigureAwait(false);
        await _delay(TimeSpan.FromSeconds(verb.Number("--seconds") ?? 5), token).ConfigureAwait(false);
        await ApplyAsync("stop", verb, letter => index.StopWatchingAsync(letter, token),
            letters => index.StopWatchingAsync(letters, token), () => index.StopWatchingAsync(token)).ConfigureAwait(false);
        index.Drives.ToList().ForEach(status => _writeLine(Describe(status)));

        if (verb.Has("--inspect-session") && opened.Session is { } session)
        {
            await opened.DisposeAsync().ConfigureAwait(false);
            _writeLine($"Session after teardown: has ended {session.HasEnded}, ended with \"{await session.Ended.ConfigureAwait(false)}\"");
        }
    }

    // journal prints sizing; with --maximum-size it grows the one named drive through the broker and reads sizing again.
    async Task JournalAsync(OpenedIndex opened, IndexVerbArguments verb)
    {
        var grow = verb.Has("--maximum-size");
        var letters = verb.OpenedDrives;
        PrintJournalSettings(opened.Index, letters);
        if (grow)
        {
            var session = opened.Session ?? throw new InvalidOperationException("Growing the journal needs the broker source.");
            var grown = await session.GrowUsnJournalAsync(letters[0], verb.Number("--maximum-size") ?? 0,
                verb.Number("--allocation-delta") ?? 0, CancellationToken.None).ConfigureAwait(false);
            _writeLine($"Grown: maximum {grown.MaximumSize}, allocation delta {grown.AllocationDelta}");
            PrintJournalSettings(opened.Index, letters);
        }
    }

    void PrintJournalSettings(FileIndex index, IEnumerable<char> letters)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var letter in letters)
        {
            try
            {
                var settings = index.QueryUsnJournalSettings(letter);
                _writeLine($"Journal {letter}: maximum {settings.MaximumSize}, allocation delta {settings.AllocationDelta}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _writeLine($"Journal {letter}: unavailable: {exception.Message}");
            }
        }
    }
}
