using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     Parks an armed scan at its first progress report until the test releases it. Starts
///     disarmed so an initial open completes without blocking.
/// </summary>
internal sealed class BlockOnFirstArmedReport : IProgress<IndexScanProgress>
{
    readonly TaskCompletionSource _reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Armed { get; set; }
    public Task Reported => _reported.Task;
    public void Release() => _release.TrySetResult();

    public void Report(IndexScanProgress value)
    {
        if (!Armed)
        {
            return;
        }

        _reported.TrySetResult();
        _release.Task.GetAwaiter().GetResult();
    }
}
