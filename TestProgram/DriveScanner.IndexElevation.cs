using MFTLib;

namespace TestProgram;

// The elevation-status verb: what this process knows about elevation, through the static helpers and
// through the provider interface the sample's other modes use, plus an explicit relaunch on request.
partial class DriveScanner
{
    void ShowElevationStatus(IndexVerbArguments verb, string[] commandLine)
    {
        IElevationProvider provider = ElevationUtilities.DefaultProvider;
        var elevated = ElevationUtilities.IsElevated();
        var canSelfElevate = ElevationUtilities.CanSelfElevate();
        _writeLine($"Elevated: {elevated} (provider says {provider.IsElevated()}).");
        _writeLine($"Can relaunch itself elevated: {canSelfElevate} (provider says {provider.CanSelfElevate()}).");
        _writeLine($"Default wait for an elevated copy: {ElevationUtilities.DefaultElevatedTimeout}.");
        _writeLine(elevated
            ? "This process is elevated, so every verb can run here without a prompt."
            : "This process is not elevated. The index verbs on the broker source launch the elevated broker, which " +
              "raises one UAC prompt; the enumeration source needs none. " +
              (canSelfElevate
                  ? "It can also relaunch itself elevated with --relaunch-elevated."
                  : $"It cannot relaunch itself: start {_getProcessPath()} from an elevated terminal instead."));

        if (!verb.Has("--relaunch-elevated"))
        {
            return;
        }

        if (elevated)
        {
            _writeLine("Already elevated; nothing to relaunch.");
        }
        else if (IsUnattended())
        {
            _writeLine($"Running unattended ({UnattendedVariableName}=1): the elevated relaunch was skipped.");
        }
        else if (ConfirmElevation(commandLine, SelfElevationReason))
        {
            _writeLine($"Elevated relaunch {(ElevationUtilities.TryRunElevated(commandLine, ElevationUtilities.DefaultElevatedTimeout) ? "succeeded" : "failed")}.");
        }
        else
        {
            _writeLine("The elevated relaunch was cancelled at the heads-up dialog.");
        }
    }
}
