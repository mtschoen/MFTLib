// Public types whose constructors may or may not initialize their automatic properties, so PublicMemberCallerTests
// can prove the reader credits a property through a constructor only when that constructor's own IL stores one of
// its arguments straight into the property's backing field. The reader is pointed at the test assembly by file path
// with this namespace as its surface. Nothing in production references it and nothing should.
using System.Text;

namespace MFTLib.Tests.CallerGateFixtures;

// A parameter with the property's exact name and type that is never stored.
public record CallerGateIgnoredRecord
{
    public CallerGateIgnoredRecord(int Value)
    {
        Echo = Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public int Value { get; init; }

    public string Echo { get; }
}

// The only constructor with a Value parameter delegates and never stores it.
public record CallerGateOverloadRecord(int Seed)
{
    public CallerGateOverloadRecord(int Value, bool flag) : this(Value + (flag ? 1 : 0))
    {
    }

    public int Value { get; init; }
}

// The positional and the replaced property are stored from arguments; the constant one is not.
public record CallerGateInitializedRecord(int First, int Second)
{
    public int First { get; init; } = First;

    public int Constant { get; init; } = 42;

    public int Total => First + Second + Constant;
}

// An argument is stored straight into a field, but not the field the property reads.
public record CallerGateOtherFieldRecord
{
    readonly int _other;

    public CallerGateOtherFieldRecord(int Value)
    {
        _other = Value;
    }

    public int Value { get; init; }

    public int Other => _other;
}

// The constructor with the matching parameter is not public.
public record CallerGateInternalConstructorRecord
{
    internal CallerGateInternalConstructorRecord(int Value)
    {
        Stored = Value;
    }

    public int Stored { get; }
}

// The constructor assigns through the init accessor, which stores nothing itself.
public record CallerGateSetterRecord
{
    public CallerGateSetterRecord(int value)
    {
        Value = value;
    }

    public int Value { get; init; }
}

public record CallerGateGenericRecord<T>(T Item);

public record CallerGateGenericIgnoredRecord<T>
{
    public CallerGateGenericIgnoredRecord(T Value)
    {
        Echo = Value;
    }

    public T? Value { get; init; }

    public T Echo { get; }
}

public readonly record struct CallerGateStructRecord(int Amount);

public record struct CallerGateStructIgnoredRecord
{
    public CallerGateStructIgnoredRecord(int Value)
        : this()
    {
        Echo = Value;
    }

    public int Value { get; init; }

    public int Echo { get; }
}

// Constructors that name the argument and the field but do not run straight through to the store.
public record CallerGateStraightRecord
{
    public CallerGateStraightRecord(int value)
    {
        Value = value;
    }

    public int Value { get; }
}

// Delegates with this(...) and also stores an argument itself.
public record CallerGateDelegatingStoreRecord(int Seed)
{
    public CallerGateDelegatingStoreRecord(int value, string label) : this(label.Length)
    {
        Value = value;
    }

    public int Value { get; }
}

public record CallerGateMutatedRecord
{
    public CallerGateMutatedRecord(int value)
    {
        value += 1;
        Value = value;
    }

    public int Value { get; }
}

public record CallerGateOverwrittenRecord
{
    public CallerGateOverwrittenRecord(int value)
    {
        Value = value;
        Value = 0;
    }

    public int Value { get; }
}

public record CallerGateConditionalRecord
{
    public CallerGateConditionalRecord(int value, bool store)
    {
        if (store)
        {
            Value = value;
        }
    }

    public int Value { get; }
}

public record CallerGateGuardedRecord
{
    public CallerGateGuardedRecord(int value)
    {
        if (value < 0)
        {
            throw new InvalidOperationException("negative");
        }

        Value = value;
    }

    public int Value { get; }
}

// Not a record: a private PrintMembers must not make it one.
public class CallerGatePlainClass
{
    public CallerGatePlainClass(int Value)
    {
        Stored = Value;
    }

    public int Stored { get; }

    public static bool operator ==(CallerGatePlainClass? left, CallerGatePlainClass? right) =>
        left?.Stored == right?.Stored;

    public static bool operator !=(CallerGatePlainClass? left, CallerGatePlainClass? right) => !(left == right);

    public override bool Equals(object? obj) => obj is CallerGatePlainClass other && other.Stored == Stored;

    public override int GetHashCode() => Stored;

    public string Describe() => PrintMembers().ToString(System.Globalization.CultureInfo.InvariantCulture);

    int PrintMembers() => Stored;
}

// Authored versions of every member a record could have synthesized; the type is still a record.
public record CallerGateAuthoredEverythingRecord(int Id)
{
    public virtual bool Equals(CallerGateAuthoredEverythingRecord? other) => other is not null && Id == other.Id;

    public override int GetHashCode() => Id;

    public override string ToString() => "authored";

    protected virtual bool PrintMembers(StringBuilder builder)
    {
        builder.Append(Id);
        return true;
    }
}

public readonly record struct CallerGateAuthoredEverythingStruct(int Id)
{
    public bool Equals(CallerGateAuthoredEverythingStruct other) => Id == other.Id;

    public override int GetHashCode() => Id;

    public override string ToString() => "authored";

    bool PrintMembers(StringBuilder builder)
    {
        builder.Append(Id);
        return true;
    }
}

// Reads every property the fixtures declare only to be constructed, so no unused-accessor warning hides in them.
internal static class CallerGateFixtureConstructorReads
{
    internal static object Read(CallerGateIgnoredRecord ignored, CallerGateOverloadRecord overload,
        CallerGateInternalConstructorRecord internalConstructor, CallerGateSetterRecord setter,
        CallerGateGenericRecord<int> generic, CallerGateGenericIgnoredRecord<int> genericIgnored,
        CallerGateStructRecord structure, CallerGateStructIgnoredRecord structureIgnored,
        CallerGateOtherFieldRecord otherField, CallerGateStraightRecord straight, CallerGateMutatedRecord mutated,
        CallerGateOverwrittenRecord overwritten, CallerGateConditionalRecord conditional,
        CallerGateGuardedRecord guarded, CallerGateDelegatingStoreRecord delegating) =>
        (ignored.Echo, overload.Seed, internalConstructor.Stored, setter.Value, generic.Item, genericIgnored.Echo,
            structure.Amount, structureIgnored.Echo, otherField.Value, straight.Value, mutated.Value, overwritten.Value,
            conditional.Value, guarded.Value, delegating.Value, delegating.Seed);

    internal static CallerGateOtherFieldRecord Initialize() => new(1) { Value = 2 };
}
