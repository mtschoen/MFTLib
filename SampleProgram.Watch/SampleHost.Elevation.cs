namespace SampleProgram.Watch;

internal enum ElevationNeed { None, BrokerLaunch }

partial class SampleHost
{
    const string BrokerLaunchReason = "SampleProgram launches its broker elevated.";

    internal int RunWithElevation(string[] arguments, ElevationNeed need, Func<int> run)
    {
        if (need is ElevationNeed.None || _isElevated())
        {
            return run();
        }

        if (IsUnattended())
        {
            return SkipElevationUnattended(arguments);
        }

        if (ConfirmElevation(arguments, BrokerLaunchReason))
        {
            return run();
        }

        PrintElevationFailure(arguments);
        return 1;
    }
}
