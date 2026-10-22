namespace MFTLib;

/// <summary>
///     How a source tells its caller what its operation is doing. A pending read cannot tell a
///     quiet volume from a wedged step, so the source says which one it is in.
/// </summary>
interface ISourceOperationReporter
{
    /// <summary>The source is blocked in a volume read: a quiet volume, never a stall.</summary>
    void WaitingOnVolume();

    /// <summary>The source is working on <paramref name="stepName" />; restarts the processing clock.</summary>
    void Processing(string stepName);
}
