using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>Observes an actual publication leaving the write gate, without assuming a synchronous rescan prefix.</summary>
static class IndexPublication
{
    internal static Task ObserveNextAsync(FileIndex index)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = index.Hooks.PublishInsideWriteGate;
        index.Hooks.PublishInsideWriteGate = root =>
        {
            previous?.Invoke(root);
            entered.TrySetResult();
        };
        return AfterWriteGateAsync(index, entered.Task);
    }

    static async Task AfterWriteGateAsync(FileIndex index, Task entered)
    {
        await entered.WaitAsync(HangGuard);
        await index.WaitForRootWriteGateForTest(index.RootKey).WaitAsync(HangGuard);
        index.ReleaseRootWriteGateForTest(index.RootKey);
    }
}
