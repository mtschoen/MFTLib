namespace MFTLib.Index;

/// <summary>
///     The batched entry points: each fans the single-drive operation out over the requested drives
///     concurrently, waits for every one to settle, and returns one result per drive in the order
///     given. They throw only for caller errors (a null list, a duplicate letter, a letter not in the
///     index, a disposed index) and for cancellation, which is thrown after every per-drive
///     operation has settled, so nothing a call started outlives it.
/// </summary>
public sealed partial class FileIndex
{
    /// <summary>
    ///     Starts the watch of each listed drive; see <see cref="StartWatchingAsync(char, CancellationToken)" />.
    ///     A drive with no MFT-backed block is <see cref="DriveOperationOutcome.NotApplicable" /> and
    ///     records no watch request, so a later rescan does not start it.
    /// </summary>
    public Task<IReadOnlyList<DriveOperationResult>> StartWatchingAsync(IReadOnlyList<char> driveLetters,
        CancellationToken cancellationToken)
    {
        if (RejectedTask<IReadOnlyList<DriveOperationResult>>(nameof(StartWatchingAsync)) is { } rejected)
        {
            return rejected;
        }

        return RunBatchAsync(driveLetters, HasWatchableBlock, letter => StartWatchingAsync(letter, cancellationToken),
            cancellationToken);
    }

    /// <summary>Starts the watch of every drive, in <see cref="FileIndexOptions.Drives" /> order.</summary>
    public Task<IReadOnlyList<DriveOperationResult>> StartWatchingAsync(CancellationToken cancellationToken)
    {
        if (RejectedTask<IReadOnlyList<DriveOperationResult>>(nameof(StartWatchingAsync)) is { } rejected)
        {
            return rejected;
        }

        return StartWatchingAsync(AllDriveLetters(), cancellationToken);
    }

    /// <summary>Stops the watch of each listed drive; see <see cref="StopWatchingAsync(char, CancellationToken)" />.</summary>
    public Task<IReadOnlyList<DriveOperationResult>> StopWatchingAsync(IReadOnlyList<char> driveLetters,
        CancellationToken cancellationToken)
    {
        if (RejectedTask<IReadOnlyList<DriveOperationResult>>(nameof(StopWatchingAsync)) is { } rejected)
        {
            return rejected;
        }

        return RunBatchAsync(driveLetters, IsWatching, letter => StopWatchingAsync(letter, cancellationToken),
            cancellationToken);
    }

    /// <summary>Stops the watch of every drive, in <see cref="FileIndexOptions.Drives" /> order.</summary>
    public Task<IReadOnlyList<DriveOperationResult>> StopWatchingAsync(CancellationToken cancellationToken)
    {
        if (RejectedTask<IReadOnlyList<DriveOperationResult>>(nameof(StopWatchingAsync)) is { } rejected)
        {
            return rejected;
        }

        return StopWatchingAsync(AllDriveLetters(), cancellationToken);
    }

    /// <summary>Rescans each listed drive; see <see cref="RescanAsync(char, CancellationToken)" />.</summary>
    public Task<IReadOnlyList<DriveOperationResult>> RescanAsync(IReadOnlyList<char> driveLetters,
        CancellationToken cancellationToken)
    {
        if (RejectedTask<IReadOnlyList<DriveOperationResult>>(nameof(RescanAsync)) is { } rejected)
        {
            return rejected;
        }

        return RunBatchAsync(driveLetters, applicable: null, letter => RescanAsync(letter, cancellationToken),
            cancellationToken);
    }

    /// <summary>Rescans every drive, in <see cref="FileIndexOptions.Drives" /> order.</summary>
    public Task<IReadOnlyList<DriveOperationResult>> RescanAsync(CancellationToken cancellationToken)
    {
        if (RejectedTask<IReadOnlyList<DriveOperationResult>>(nameof(RescanAsync)) is { } rejected)
        {
            return rejected;
        }

        return RescanAsync(AllDriveLetters(), cancellationToken);
    }

    /// <summary>
    ///     Waits for the initial catch-up of each listed drive; see
    ///     <see cref="WaitForCatchUpAsync(char, CancellationToken)" />. A drive whose watch faults
    ///     or is superseded is <see cref="DriveOperationOutcome.Failed" /> and does not end the wait
    ///     for the others.
    /// </summary>
    public Task<IReadOnlyList<DriveOperationResult>> WaitForCatchUpAsync(IReadOnlyList<char> driveLetters,
        CancellationToken cancellationToken)
    {
        var letters = ValidateBatch(driveLetters);
        Task?[] waits;
        lock (_stateLock)
        {
            waits = [.. letters.Select(letter => GetCatchUpWaitLocked(GetDriveRuntime(letter)))];
        }

        if (!cancellationToken.IsCancellationRequested && waits.Any(wait => wait is { IsCompleted: false }) &&
            RejectedTask<IReadOnlyList<DriveOperationResult>>(nameof(WaitForCatchUpAsync)) is { } rejected)
        {
            return rejected;
        }

        return new BatchedCatchUpWait(letters, waits, cancellationToken, DisposalToken).Completion;
    }

    /// <summary>
    ///     A test seam: invoked by the no-list catch-up wait before it resolves the drive
    ///     list, so a test can begin disposal in exactly that window.
    /// </summary>
    internal Action? BeforeNoListWaitResolvesDrivesForTest { get; set; }

    /// <summary>Waits for the initial catch-up of every drive, in <see cref="FileIndexOptions.Drives" /> order.</summary>
    public Task<IReadOnlyList<DriveOperationResult>> WaitForCatchUpAsync(CancellationToken cancellationToken)
    {
        // A disposed index settles no drive's wait, and disposal can begin at any step before the
        // per-drive settle check, so from inside a handler a disposal failure of any of those
        // steps answers with the guard's exception instead.
        try
        {
            BeforeNoListWaitResolvesDrivesForTest?.Invoke();
            return WaitForCatchUpAsync(AllDriveLetters(), cancellationToken);
        }
        catch (ObjectDisposedException disposal) when (IsInsideHandlerOfThisIndex())
        {
            return Task.FromException<IReadOnlyList<DriveOperationResult>>(
                RejectInsideHandler(nameof(WaitForCatchUpAsync)) ?? disposal);
        }
    }

    /// <summary>Whether a start applies: the source offers a watch and the drive has an MFT-backed block to watch from.</summary>
    bool HasWatchableBlock(char driveLetter)
    {
        lock (_stateLock)
        {
            return WatchSourceOrNull is not null && FindWatchableDriveBlockLocked(driveLetter) is not null;
        }
    }

    /// <summary>Whether a stop applies: the drive is watching, or its watch is requested or retiring.</summary>
    bool IsWatching(char driveLetter)
    {
        lock (_stateLock)
        {
            return IsWatchingLocked(GetDriveRuntime(driveLetter));
        }
    }

    static bool IsWatchingLocked(DriveRuntime runtime) =>
        runtime.WatchRequested || runtime.Current is not null || runtime.Retiring is not null;

    char[] AllDriveLetters()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return [.. _options.Drives.Select(drive => drive.DriveLetter)];
    }

    /// <summary>
    ///     Validates the list, runs <paramref name="operation" /> for every drive it applies to
    ///     (<paramref name="applicable" />, or all when null) concurrently, reports the others
    ///     <see cref="DriveOperationOutcome.NotApplicable" />, and returns once all have settled.
    ///     A cancelled token then throws, whatever the results say.
    /// </summary>
    async Task<IReadOnlyList<DriveOperationResult>> RunBatchAsync(IReadOnlyList<char> driveLetters,
        Func<char, bool>? applicable, Func<char, Task> operation, CancellationToken cancellationToken)
    {
        var letters = ValidateBatch(driveLetters);
        var results = await Task.WhenAll(letters.Select(letter => RunOneAsync(letter, applicable, operation)))
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>
    ///     The caller errors a batched call throws before it starts anything: a null list, a disposed
    ///     index, a letter not in the index, a letter listed twice.
    /// </summary>
    char[] ValidateBatch(IReadOnlyList<char> driveLetters)
    {
        ArgumentNullException.ThrowIfNull(driveLetters);
        ObjectDisposedException.ThrowIf(_disposed, this);
        HashSet<char> seen = [];
        foreach (var letter in driveLetters)
        {
            var upperLetter = char.ToUpperInvariant(letter);
            if (!_driveRuntimes.ContainsKey(upperLetter))
            {
                throw new ArgumentException($"Drive {letter} is not part of this index.", nameof(driveLetters));
            }

            if (!seen.Add(upperLetter))
            {
                throw new ArgumentException($"Drive {letter} is listed more than once.", nameof(driveLetters));
            }
        }

        return [.. driveLetters];
    }

    static async Task<DriveOperationResult> RunOneAsync(char driveLetter, Func<char, bool>? applicable,
        Func<char, Task> operation)
    {
        try
        {
            if (applicable is not null && !applicable(driveLetter))
            {
                return new DriveOperationResult(driveLetter, DriveOperationOutcome.NotApplicable, null);
            }

            await operation(driveLetter).ConfigureAwait(false);
            return new DriveOperationResult(driveLetter, DriveOperationOutcome.Succeeded, null);
        }
        catch (Exception failure)
        {
            return new DriveOperationResult(driveLetter, DriveOperationOutcome.Failed, failure);
        }
    }
}
