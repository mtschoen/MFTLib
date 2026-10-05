namespace MFTLib;

/// <summary>
///     Seam for the work the elevated child-process broker mode performs, so
///     <see cref="ElevatedEntryPoint.TryHandle(string[], IElevatedEntryRunner)" /> can be unit-tested without spawning
///     a real elevated process, opening a real pipe, or running a real scan. The
///     production implementation is <see cref="DefaultElevatedEntryRunner" />.
/// </summary>
internal interface IElevatedEntryRunner
{
    /// <summary>
    ///     Serve the journal broker over the control pipe <paramref name="controlPipeName" /> the
    ///     non-elevated caller created, until that pipe closes. Drive pipes are named on the
    ///     control pipe as the caller opens them.
    /// </summary>
    void RunBroker(string? controlPipeName);
}
