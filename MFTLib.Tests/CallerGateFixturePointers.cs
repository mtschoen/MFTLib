// Two overloads whose signatures differ only in a function pointer type. The reader renders every function pointer
// as `fnptr`, so both overloads share one key; PublicMemberCallerTests uses this to prove the key-uniqueness check
// fires. It lives in its own namespace so the other fixture controls do not read it. Nothing in production references
// it and nothing should.
namespace MFTLib.Tests.CallerGateFixturePointers;

public static unsafe class CallerGateFunctionPointers
{
    public static void Choose(delegate*<int, void> callback)
    {
        callback(1);
    }

    public static void Choose(delegate*<string, void> callback)
    {
        callback("one");
    }
}
