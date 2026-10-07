// Value types and an interface whose ToString, Equals and GetHashCode are called through constrained callvirts built
// by CallerGateGeneratedAssembly, so PublicMemberCallerTests can prove which override a constrained call credits.
// The reader is pointed at the test assembly by file path with this namespace as its surface. Nothing in production
// references it and nothing should.
namespace MFTLib.Tests.CallerGateFixtures;

public interface ICallerGateText
{
    string ToString();
}

public readonly struct CallerGateOrdinaryText : IEquatable<CallerGateOrdinaryText>
{
    public override string ToString() => "ordinary";

    public override bool Equals(object? obj) => obj is CallerGateOrdinaryText;

    public bool Equals(CallerGateOrdinaryText other) => true;

    public override int GetHashCode() => 1;

    public static bool operator ==(CallerGateOrdinaryText left, CallerGateOrdinaryText right) => left.Equals(right);

    public static bool operator !=(CallerGateOrdinaryText left, CallerGateOrdinaryText right) => !left.Equals(right);
}

public readonly struct CallerGateInterfaceText : ICallerGateText
{
    string ICallerGateText.ToString() => "interface";

    public override string ToString() => "override";
}

public readonly struct CallerGateGenericText<T>
{
    public override string ToString() => typeof(T).Name;
}

public readonly struct CallerGateNoOverrideText
{
    public int Value { get; init; }
}

// ToString hides the Object method instead of overriding it, so a constrained Object.ToString never reaches it.
public class CallerGateHiddenText
{
    public new string ToString() => GetType().Name + "hidden";
}

// ToString is virtual but takes a new slot, so it does not override Object.ToString either.
public class CallerGateHiddenVirtualText
{
    public new virtual string ToString() => GetType().Name + "hidden";
}

// The Object slots are hidden by new virtual methods one level up, so overriding them in a descendant reuses the
// hiding slot and never the Object one.
public class CallerGateHiddenBase
{
    public new virtual string ToString() => "base";

    public new virtual bool Equals(object? obj) => obj is CallerGateHiddenBase;

    public new virtual int GetHashCode() => 1;
}

public class CallerGateHiddenDerived : CallerGateHiddenBase
{
    public override string ToString() => "derived";

    public override bool Equals(object? obj) => obj is CallerGateHiddenDerived;

    public override int GetHashCode() => 2;
}

// A plain override chain with no hiding anywhere: both levels override the Object slot.
public class CallerGatePlainBase
{
    public override string ToString() => "base";

    // Same name, different signature, new slot: it does not hide Equals(object).
    public virtual bool Equals(CallerGatePlainBase? other) => other is not null;
}

public class CallerGatePlainDerived : CallerGatePlainBase
{
    public override string ToString() => "derived";

    public override bool Equals(object? obj) => obj is CallerGatePlainDerived;

    public override int GetHashCode() => 3;
}

// A generic base cannot be followed to Object, so the override below is refused.
public class CallerGateGenericBase<T>
{
    public override string ToString() => typeof(T).Name;
}

public class CallerGateGenericChild : CallerGateGenericBase<int>
{
    public override string ToString() => "child";
}

// The base class lives in another assembly, so the reader cannot follow the slot to Object and refuses.
public class CallerGateForeignBaseText : Random
{
    public override string ToString() => "foreign";
}
