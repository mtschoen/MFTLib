using MFTLib;

namespace SampleProgram.Direct;

// The exclude-defender verb adds this sample's own executable to the Windows Defender process exclusions through the
// library's elevation sequence. It changes machine state, so it only ever runs when asked for by name.
partial class SampleHost
{
    internal const string ExcludeDefenderVerb = "exclude-defender";
    internal const string DefenderProcessName = "SampleProgram.Direct.exe";

    internal Func<bool> _isWindows = OperatingSystem.IsWindows;

    static bool IsExcludeDefender(string[] arguments)
    {
        return arguments is [var verb] && verb.Equals(ExcludeDefenderVerb, StringComparison.OrdinalIgnoreCase);
    }

    const string DefenderNeed = "Adding a Windows Defender process exclusion needs administrator rights, so ";
    const string DefenderReason = "SampleProgram.Direct asks Windows to run the exclusion elevated.";

    // An elevated run is the relaunched child (or an elevated terminal): its output goes to output.log and it never
    // waits for a key, like every other elevated verb. An unelevated run is skipped when unattended and otherwise
    // shows the heads-up once, before the library raises the UAC prompt on either of its elevation routes. The
    // relaunched child reaches the same member and, being elevated, runs the exclusions itself.
    int RunExcludeDefender(string[] arguments)
    {
        if (_isElevated())
        {
            return RunWithOutputLog(() => ApplyDefenderExclusion(arguments));
        }

        if (IsUnattended())
        {
            _output.WriteLine($"Running unattended ({UnattendedVariableName}=1): elevation skipped, no prompt shown.");
            _output.WriteLine($"Run {ExcludeDefenderVerb} from an ELEVATED terminal to add the Windows Defender exclusion.");
            return 1;
        }

        if (_isWindows() && !ConfirmElevation(arguments, DefenderReason, DefenderNeed))
        {
            _output.WriteLine("Cancelled: no elevation was requested and no Windows Defender exclusion was added.");
            return 1;
        }

        return ApplyDefenderExclusion(arguments);
    }

    int ApplyDefenderExclusion(string[] arguments)
    {
        var applied = ElevationUtilities.TryAddDefenderProcessExclusions(
            [DefenderProcessName], arguments, new HostElevationProvider(this));
        _output.WriteLine(applied
            ? $"Windows Defender process exclusion added: {DefenderProcessName}."
            : "The Windows Defender process exclusion was not added.");
        return applied ? 0 : 1;
    }

    // The host's elevation functions as the provider the library takes, so a test substitutes them in one place.
    sealed class HostElevationProvider(SampleHost host) : IElevationProvider
    {
        public bool IsElevated() => host._isElevated();

        public bool CanSelfElevate() => host._canSelfElevate();

        public bool TryRunElevated(IReadOnlyList<string> arguments, TimeSpan timeout) => host._tryRunElevated(arguments, timeout);
    }
}
