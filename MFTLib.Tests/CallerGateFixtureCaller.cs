// References one broker constructor so the caller gate can distinguish overloads.
// The collector reads this method's IL; tests never invoke it.
using System.Runtime.Versioning;
using MFTLib.Index;

namespace MFTLib.Tests;

static class CallerGateFixtureCaller
{
    [SupportedOSPlatform("windows")]
    internal static BrokerSession CreateBrokerSession(
        Func<string, bool> launchBroker, TimeSpan? connectTimeout)
        => new BrokerSession(launchBroker, connectTimeout);
}

// ToString called on a library struct that declares an override: C# emits `constrained. T` then callvirt Object.ToString.
static class CallerGateFixtureExplicitToString
{
    internal static string Describe(FileEntry entry) => entry.ToString();
}

// Interpolation formats through AppendFormatted<T> and names no ToString.
static class CallerGateFixtureInterpolatedToString
{
    internal static string Describe(FileEntry entry) => $"entry {entry}";
}

// ToString on a library value type that declares no override of its own.
static class CallerGateFixtureInheritedToString
{
    internal static string Describe(ProducerKind kind) => kind.ToString();
}
