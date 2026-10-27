namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Follows the root's catch-up task directly, settles its result when the task ends or when
    ///     a token cancels it, and completes asynchronously. The completion source is created with
    ///     <see cref="TaskCreationOptions.RunContinuationsAsynchronously" />, and the caller's token
    ///     and the disposal token reach it through registrations, never through
    ///     <see cref="Task.WaitAsync(CancellationToken)" />, so settling from a pump's
    ///     fault or a handler's cancellation never runs the awaiter's code on that stack.
    /// </summary>
    sealed class CatchUpWait
    {
        readonly RootKey _drive;
        readonly CancellationToken _callerToken;
        readonly CancellationToken _disposalToken;
        readonly TaskCompletionSource<DriveOperationResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _settled;

        public CatchUpWait(RootKey drive, Task? wait, CancellationToken callerToken,
            CancellationToken disposalToken)
        {
            _drive = drive;
            _callerToken = callerToken;
            _disposalToken = disposalToken;
            Follow(wait);
            if (_completion.Task.IsCompleted)
            {
                return;
            }

            var callerRegistration = callerToken.Register(() => Cancel(callerToken));
            var disposalRegistration = disposalToken.Register(() => Cancel(disposalToken));
            _ = _completion.Task.ContinueWith(_ =>
            {
                callerRegistration.Dispose();
                disposalRegistration.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public Task<DriveOperationResult> Completion => _completion.Task;

        void Follow(Task? wait)
        {
            if (wait is null)
            {
                Settle(DriveOperationOutcome.NotApplicable, null);
                return;
            }

            _ = wait.ContinueWith(antecedent =>
            {
                if (antecedent.Exception is { } faulted)
                {
                    Settle(DriveOperationOutcome.Failed, faulted.InnerExceptions[0]);
                }
                else if (antecedent.IsCanceled)
                {
                    Settle(DriveOperationOutcome.Failed, new TaskCanceledException(antecedent));
                }
                else
                {
                    Settle(DriveOperationOutcome.Succeeded, null);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        void Cancel(CancellationToken token) =>
            Settle(DriveOperationOutcome.Failed, new OperationCanceledException(token));

        void Settle(DriveOperationOutcome outcome, Exception? failure)
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0)
            {
                return;
            }

            var result = new DriveOperationResult(_drive, outcome, failure);
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
                _completion.TrySetResult(result);
            }
        }
    }
}
