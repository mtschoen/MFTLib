// This file is deliberately in namespace MFTLib.Index, inside the test assembly, and it
// deliberately references a type the MFTLib.Index namespace boundary forbids. It exists only so
// NamespaceBoundaryTests can prove the boundary rule reports a violation when one is present. A
// green boundary rule with no negative control proves nothing, which is why MFTLib#118 makes this
// fixture mandatory. Nothing in production references it and nothing should.
namespace MFTLib.Index;

static class NamespaceBoundaryViolationFixture
{
    internal static MftRecord ForbiddenReference()
    {
        return default;
    }
}
