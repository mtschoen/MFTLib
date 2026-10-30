namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class IndexView
    {
        int _registryDetached;

        internal bool DetachRegistry() => Interlocked.Exchange(ref _registryDetached, 1) == 0 &&
            (Scan.Key is not { } key || Registry.Detach(key, Scan));

        internal async Task DetachAsync()
        {
            Scan.RemoveView(this);
            if (DetachRegistry())
            {
                await Scan.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
