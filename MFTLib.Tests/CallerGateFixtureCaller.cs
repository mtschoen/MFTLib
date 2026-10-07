// Calls exactly one overload of a library overload set and nothing else, so PublicMemberCallerTests can prove
// the reference collector tells overloads apart and reports a member that no IL names. Nothing in production
// references it and nothing should. The delegate is built but never invoked.
using MFTLib.Index;

namespace MFTLib.Tests;

static class CallerGateFixtureCaller
{
    internal static Func<string, IReadOnlySet<char>?, Action<CachedBlockRejection>?, IReadOnlyList<CachedBlockStatus>>
        ReferenceThreeParameterInspectCached() => CacheDirectory.InspectCached;
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
