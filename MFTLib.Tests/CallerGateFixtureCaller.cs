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
