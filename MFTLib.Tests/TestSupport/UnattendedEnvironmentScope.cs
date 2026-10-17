using SampleProgram.Direct;

namespace MFTLib.Tests.TestSupport;

// Sets (or clears, with null) the process-wide unattended variable and restores the previous value on dispose. A test
// class that uses it must carry [DoNotParallelize] because the environment is process-global.
internal sealed class UnattendedEnvironmentScope : IDisposable
{
    readonly string? _previous = Environment.GetEnvironmentVariable(SampleHost.UnattendedVariableName);

    public UnattendedEnvironmentScope(string? value)
    {
        Environment.SetEnvironmentVariable(SampleHost.UnattendedVariableName, value);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SampleHost.UnattendedVariableName, _previous);
    }
}
