// A public positional record with an authored second Deconstruct overload and an authored property, so
// PublicMemberCallerTests can prove the metadata reader does not mistake the authored members for generated ones.
// The reader is pointed at the test assembly by file path with this namespace as its surface. Nothing in
// production references it and nothing should.
namespace MFTLib.Tests.CallerGateFixtures;

public sealed record CallerGatePositionalRecord(int First, int Second)
{
    public int Extra { get; init; }

    public int Total => First + Second;

    public static CallerGatePositionalRecord Create() => new(1, 2) { Extra = 3 };

    public void Deconstruct(out int extra) => extra = Extra;
}
