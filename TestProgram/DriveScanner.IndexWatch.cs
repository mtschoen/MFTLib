using MFTLib.Index;

namespace TestProgram;

// The lifecycle verbs: rescan and watch. Both take the same scope: one drive, a list of drives or every
// drive, which selects one of the three public overloads of each lifecycle call.
partial class DriveScanner
{
    // How the watch verb waits out its observation window; a test replaces it so nothing sleeps.
    internal Func<TimeSpan, CancellationToken, Task> _delay = Task.Delay;

    /// <summary>The drives a lifecycle call applies to: one letter, a list, or (both null) every drive.</summary>
    internal readonly record struct LifecycleScope(char? Single, IReadOnlyList<char>? List)
    {
        internal string Describe()
        {
            return Single is { } letter ? $"single drive {letter}"
                : List is { } letters ? $"drive list [{string.Join(", ", letters)}]"
                : "all drives";
        }
    }

    static LifecycleScope ScopeOf(IndexVerbArguments verb)
    {
        if (verb.Text("--drive-scope") is { } single)
        {
            return new LifecycleScope(IndexVerbArguments.TryParseDrive(single, out var letter) ? letter : null, null);
        }

        return new LifecycleScope(null, verb.Letters("--drive-list"));
    }

    async Task RescanAsync(FileIndex index, IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        var scope = ScopeOf(verb);
        _writeLine($"Rescanning {scope.Describe()}.");
        if (scope.Single is { } letter)
        {
            await RunSingleDriveCallAsync($"RescanAsync({letter})", () => index.RescanAsync(letter, cancellationToken))
                .ConfigureAwait(false);
        }
        else
        {
            PrintOperationResults(scope.List is { } letters
                ? await index.RescanAsync(letters, cancellationToken).ConfigureAwait(false)
                : await index.RescanAsync(cancellationToken).ConfigureAwait(false));
        }

        PrintAllDriveStatuses(index);
    }

    async Task WatchAsync(OpenedIndex opened, IndexVerbArguments verb, CancellationToken cancellationToken)
    {
        var index = opened.Index;
        index.Changed += change => _writeLine($"  {FormatFileChange(change)}");
        index.WatchStateChanged += state => _writeLine($"  {FormatWatchState(state)}");
        index.WatchFaulted += fault => _writeLine($"  {FormatWatchFault(fault)}");
        var scope = ScopeOf(verb);
        _writeLine($"Watching {scope.Describe()}.");

        await StartWatchingAsync(index, scope, cancellationToken).ConfigureAwait(false);
        await WaitForCatchUpAsync(index, scope, cancellationToken).ConfigureAwait(false);
        PrintAllDriveStatuses(index);

        var seconds = verb.Number("--seconds") ?? TestProgramArguments.DefaultWatchSeconds;
        _writeLine($"Observing changes for {seconds} seconds.");
        await _delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);

        await StopWatchingAsync(index, scope, cancellationToken).ConfigureAwait(false);
        PrintAllDriveStatuses(index);
        foreach (var status in index.Drives)
        {
            PrintRecoveryExplanation(status);
        }

        if (verb.Has("--inspect-session"))
        {
            await InspectSessionAfterTeardownAsync(opened, cancellationToken).ConfigureAwait(false);
        }
    }

    async Task StartWatchingAsync(FileIndex index, LifecycleScope scope, CancellationToken cancellationToken)
    {
        if (scope.Single is { } letter)
        {
            await RunSingleDriveCallAsync($"StartWatchingAsync({letter})",
                () => index.StartWatchingAsync(letter, cancellationToken)).ConfigureAwait(false);
            return;
        }

        PrintOperationResults(scope.List is { } letters
            ? await index.StartWatchingAsync(letters, cancellationToken).ConfigureAwait(false)
            : await index.StartWatchingAsync(cancellationToken).ConfigureAwait(false));
    }

    async Task WaitForCatchUpAsync(FileIndex index, LifecycleScope scope, CancellationToken cancellationToken)
    {
        if (scope.Single is { } letter)
        {
            await RunSingleDriveCallAsync($"WaitForCatchUpAsync({letter})",
                () => index.WaitForCatchUpAsync(letter, cancellationToken)).ConfigureAwait(false);
            return;
        }

        PrintOperationResults(scope.List is { } letters
            ? await index.WaitForCatchUpAsync(letters, cancellationToken).ConfigureAwait(false)
            : await index.WaitForCatchUpAsync(cancellationToken).ConfigureAwait(false));
    }

    async Task StopWatchingAsync(FileIndex index, LifecycleScope scope, CancellationToken cancellationToken)
    {
        if (scope.Single is { } letter)
        {
            await RunSingleDriveCallAsync($"StopWatchingAsync({letter})",
                () => index.StopWatchingAsync(letter, cancellationToken)).ConfigureAwait(false);
            return;
        }

        PrintOperationResults(scope.List is { } letters
            ? await index.StopWatchingAsync(letters, cancellationToken).ConfigureAwait(false)
            : await index.StopWatchingAsync(cancellationToken).ConfigureAwait(false));
    }

    // A single-drive call reports failure by throwing, so its outcome is printed the way the batch results are.
    async Task RunSingleDriveCallAsync(string call, Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
            _writeLine($"  {call} completed.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _writeLine($"  {call} failed: {exception.GetType().Name}: {exception.Message}");
            if (exception is JournalCatchUpLostException lost)
            {
                _writeLine($"    {ExplainCatchUpLoss(lost)}");
            }
        }
    }

    void PrintOperationResults(IReadOnlyList<DriveOperationResult> results)
    {
        foreach (var result in results)
        {
            _writeLine($"  {FormatOperationResult(result)}");
            if (result.Failure is JournalCatchUpLostException lost)
            {
                _writeLine($"    {ExplainCatchUpLoss(lost)}");
            }
        }
    }

    void PrintAllDriveStatuses(FileIndex index)
    {
        foreach (var status in index.Drives)
        {
            PrintDriveStatus(status);
        }
    }

    void PrintRecoveryExplanation(DriveStatus status)
    {
        if (status.CheckpointLoss is null && status.ConsecutiveLostCatchUps == 0)
        {
            _writeLine($"  drive {status.DriveLetter}: no lost catch-up; the journal checkpoint is intact.");
            return;
        }

        var stopped = status.ConsecutiveLostCatchUps >= FileIndex.LostCatchUpRecoveryLimit;
        _writeLine($"  drive {status.DriveLetter}: {status.ConsecutiveLostCatchUps} consecutive lost catch-ups of the " +
                   $"{FileIndex.LostCatchUpRecoveryLimit} allowed; " +
                   (stopped ? "automatic recovery has stopped and only a rescan restarts the watch."
                            : "automatic recovery continues."));
        if (status.CheckpointLoss is { } loss)
        {
            _writeLine($"    {FormatCheckpointLoss(loss)}");
        }
    }

    // Disposes the index and then the broker session, and reports what the session says afterwards.
    async Task InspectSessionAfterTeardownAsync(OpenedIndex opened, CancellationToken cancellationToken)
    {
        if (opened.Session is not { } session)
        {
            _writeLine("No broker session to inspect: this source runs without one.");
            return;
        }

        await opened.DisposeAsync().ConfigureAwait(false);
        var ended = await session.Ended.WaitAsync(cancellationToken).ConfigureAwait(false);
        _writeLine($"Broker session after teardown: HasEnded {session.HasEnded}; Ended reported \"{ended}\".");
    }
}
