using MFTLib;

namespace MFTLibTestExtensions;

/// <summary>Opt-in protection against tests raising a real UAC elevation prompt.</summary>
public static class ElevationIsolation
{
    /// <summary>
    ///     Makes the two real elevated launches, <see cref="ElevationUtilities.TryRunElevated" /> and
    ///     <see cref="BrokerLauncher.Launch" />, throw <see cref="InvalidOperationException" /> naming the
    ///     executable and arguments instead of starting a process, for the remainder of this test process.
    ///     Call from the test assembly's module initializer, before running anything that can launch a broker.
    ///     Repeated calls are safe; there is no reset.
    /// </summary>
    /// <remarks>
    ///     Without this, a test that reaches either launch with default dependencies starts the test host
    ///     (or any other executable) with the <c>runas</c> verb, and the person at the keyboard sees an
    ///     unexpected UAC prompt. <see cref="ElevationUtilities.TryRunElevated" /> reports an ordinary failed
    ///     launch as <c>false</c> but lets this refusal propagate, so the test fails visibly.
    ///     Activation is in-process and is not inherited by child processes.
    /// </remarks>
    public static void ForbidElevation()
    {
        ElevationGuard.Forbid();
    }
}
