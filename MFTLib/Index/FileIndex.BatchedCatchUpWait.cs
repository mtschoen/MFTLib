namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     One batched catch-up wait: follows each named drive's catch-up task directly, settles a
    ///     drive's result when its task ends or when a token cancels it, and completes once every
    ///     drive has settled. The completion source is created with
    ///     <see cref="TaskCreationOptions.RunContinuationsAsynchronously" />, and the caller's token
    ///     and the disposal token reach it through registrations, never through
    ///     <see cref="Task.WaitAsync(CancellationToken)" />, so settling a drive from a pump's
    ///     fault or a handler's cancellation never runs the awaiter's code on that stack.
    /// </summary>
    sealed class BatchedCatchUpWait
    {
        readonly char[] _driveLetters;
        readonly DriveOperationResult[] _results;
        readonly int[] _settled;
        readonly CancellationToken _callerToken;
        readonly CancellationToken _disposalToken;
        readonly TaskCompletionSource<IReadOnlyList<DriveOperationResult>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _pending;

        /// <summary>Follows <paramref name="waits" /> and completes once every drive has settled.</summary>
        /// <param name="driveLetters">The drives to wait for, in the order results are returned.</param>
        /// <param name="waits">Each drive's catch-up task, or null for a drive with nothing to wait for.</param>
        /// <param name="callerToken">Cancels the wait; the wait then throws once every drive has settled.</param>
        /// <param name="disposalToken">The index's disposal, which cancels the wait the same way.</param>
        public BatchedCatchUpWait(char[] driveLetters, Task?[] waits, CancellationToken callerToken,
            CancellationToken disposalToken)
        {
            _driveLetters = driveLetters;
            _results = new DriveOperationResult[driveLetters.Length];
            _settled = new int[driveLetters.Length];
            _callerToken = callerToken;
            _disposalToken = disposalToken;
            _pending = driveLetters.Length;
            if (_pending == 0)
            {
                Complete();
                return;
            }

            for (var index = 0; index < waits.Length; index++)
            {
                Follow(index, waits[index]);
            }

            var callerRegistration = callerToken.Register(() => Cancel(callerToken));
            var disposalRegistration = disposalToken.Register(() => Cancel(disposalToken));
            _ = _completion.Task.ContinueWith(_ =>
            {
                callerRegistration.Dispose();
                disposalRegistration.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public Task<IReadOnlyList<DriveOperationResult>> Completion => _completion.Task;

        void Follow(int index, Task? wait)
        {
            if (wait is null)
            {
                Settle(index, DriveOperationOutcome.NotApplicable, null);
                return;
            }

            _ = wait.ContinueWith(antecedent =>
            {
                if (antecedent.Exception is { } faulted)
                {
                    Settle(index, DriveOperationOutcome.Failed, faulted.InnerExceptions[0]);
                }
                else if (antecedent.IsCanceled)
                {
                    Settle(index, DriveOperationOutcome.Failed, new TaskCanceledException(antecedent));
                }
                else
                {
                    Settle(index, DriveOperationOutcome.Succeeded, null);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        void Cancel(CancellationToken token)
        {
            for (var index = 0; index < _results.Length; index++)
            {
                Settle(index, DriveOperationOutcome.Failed, new OperationCanceledException(token));
            }
        }

        void Settle(int index, DriveOperationOutcome outcome, Exception? failure)
        {
            if (Interlocked.Exchange(ref _settled[index], 1) != 0)
            {
                return;
            }

            _results[index] = new DriveOperationResult(_driveLetters[index], outcome, failure);
            if (Interlocked.Decrement(ref _pending) == 0)
            {
                Complete();
            }
        }

        void Complete()
        {
            if (_callerToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_callerToken);
            }
            else if (_disposalToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_disposalToken);
            }
            else
            {
                _completion.TrySetResult(_results);
            }
        }
    }
}
