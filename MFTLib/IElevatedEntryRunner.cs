namespace MFTLib;

/// <summary>
/// Seam for the work the elevated child-process broker mode performs, so
/// <see cref="ElevatedEntryPoint.TryHandle"/> can be unit-tested without spawning
/// a real elevated process, opening a real pipe, or running a real scan. The
/// production implementation is <see cref="DefaultElevatedEntryRunner"/>.
/// </summary>
public interface IElevatedEntryRunner
{
    /// <summary>
    /// Serve the journal broker over the named pipe <paramref name="pipeName"/> the
    /// non-elevated caller created. When <paramref name="oneShot"/> is set the broker
    /// returns after a single arm-and-scan (a single-UAC CLI-style path); otherwise it
    /// serves until the caller sends Shutdown (a persistent GUI-style path).
    /// </summary>
    void RunBroker(string? pipeName, bool oneShot);
}
