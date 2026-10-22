// A public type whose overloads differ only by generic arity, and a property with an indexer sibling, so
// PublicMemberCallerTests can prove an exempt entry names exactly one member. Read by file path with this
// namespace as the surface. Nothing in production references it and nothing should.
namespace MFTLib.Tests.CallerGateFixtures;

public static class CallerGateOverloads
{
    public static int Choose() => 0;

    public static int Choose<T>() => typeof(T).Name.Length;
}

public sealed class CallerGateIndexers
{
    public int this[int index] => index;

    public int this[string key] => key.Length;
}
