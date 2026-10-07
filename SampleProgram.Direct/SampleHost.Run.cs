namespace SampleProgram.Direct;

// The verbs arrive in the next commit; this host only carries the shared elevation flow.
partial class SampleHost
{
    internal int Run(string[] arguments)
    {
        _writeLine("SampleProgram.Direct has no verbs yet.");
        return RunWithElevation(arguments, ElevationNeed.None, () => { });
    }
}
