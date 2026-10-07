// Public records with authored members that share a name or shape with what the compiler synthesizes, so
// PublicMemberCallerTests can prove the metadata reader drops only members carrying evidence of synthesis. The
// reader is pointed at the test assembly by file path with this namespace as its surface. Nothing in
// production references it and nothing should.
namespace MFTLib.Tests.CallerGateFixtures;

public sealed record CallerGatePositionalRecord(int First, int Second)
{
    public int Extra { get; init; }

    public int Total => First + Second;

    public CallerGatePositionalRecord(string first, string second) : this(first.Length, second.Length)
    {
    }

    public static CallerGatePositionalRecord Create() => new(1, 2) { Extra = 3 };

    public void Deconstruct(out int extra) => extra = Extra;

    public override string ToString() => $"{First}:{Second}";
}

public sealed record CallerGateReplacedPropertyRecord(int Alpha, int Beta)
{
    readonly int stored = Alpha;

    public int Alpha
    {
        get => stored;
        init => stored = value;
    }

    public int Combined => Alpha + Beta;
}

public record CallerGateCopyRecord(int Value)
{
    public CallerGateCopyRecord(CallerGateCopyRecord original)
    {
        Value = original.Value;
    }
}

public record CallerGateInheritableRecord(int Value)
{
    public int Echo => Value;
}

public sealed record CallerGateEmptyRecord();

public sealed record CallerGateAuthoredDeconstructRecord(int Left, int Right)
{
    public void Deconstruct(out int left, out int right)
    {
        left = Left;
        right = Right;
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1708", Justification = "The fixture pins that a property differing from a positional one only by case is authored.")]
public sealed record CallerGateCaseSiblingRecord(int First)
{
    public string first { get; init; } = "authored";

    public int Echo => First;
}

public record CallerGateAuthoredContractRecord(int Value)
{
    protected virtual Type EqualityContract { get; } = typeof(CallerGateAuthoredContractRecord);

    public int Echo => Value;
}

public sealed record CallerGateMixedAccessorRecord(int First)
{
    public int First { get; init => field = value + 1; } = First;
}

public record CallerGateTypeDistinctRecord
{
    public CallerGateTypeDistinctRecord(int count)
    {
        Count = new string((char)0x78, count);
    }

    public string Count { get; init; }

    public int Width => Count.Length;
}
