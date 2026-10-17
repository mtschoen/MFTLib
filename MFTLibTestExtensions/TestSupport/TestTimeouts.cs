namespace MFTLibTestExtensions;

internal static class TestTimeouts
{
    /// <summary>A guard, not a measurement: a wait that hangs fails the test instead of wedging the run.</summary>
    internal static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);
}
