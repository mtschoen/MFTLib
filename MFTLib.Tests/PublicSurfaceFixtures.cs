namespace MFTLib.Tests
{
    /// <summary>
    ///     Negative-control fixture: the enumerator must report the public and protected members and
    ///     nested types, and none of the internal, private or private protected ones.
    /// </summary>
    public class PublicSurfaceFixture
    {
        public int PublicProperty { get; protected set; }

        internal int InternalProperty { get; set; }

        public static void PublicMethod() => PrivateMethod();

        protected static void ProtectedMethod()
        {
        }

        protected internal static void ProtectedInternalMethod()
        {
        }

        internal static void InternalMethod()
        {
        }

        private protected static void PrivateProtectedMethod()
        {
        }

        static void PrivateMethod()
        {
        }

        public class NestedPublic
        {
        }

        protected class NestedProtected
        {
        }

        internal sealed class NestedInternal
        {
        }
    }

    /// <summary>Negative-control fixture: sealed positional record with public synthesized members.</summary>
    public sealed record PublicSurfaceRecordFixture(int Value);

    /// <summary>Negative-control fixture: unsealed record with protected synthesized members.</summary>
    public record OpenSurfaceRecordFixture(int Value);

    /// <summary>
    ///     Negative-control fixture: records nested under a generic outer type. The two siblings
    ///     have the same arity, so a formatter that flattens <c>Outer&lt;T&gt;.Nested&lt;U&gt;</c> to
    ///     <c>Outer&lt;T, U&gt;</c> cannot tell them apart.
    /// </summary>
    public class GenericSurfaceOuter<TOuter>
    {
        public TOuter? Current { get; init; }

        public record NestedRecord<TValue>(TValue Value);

        public record SiblingRecord<TValue>(TValue Value);
    }

    /// <summary>Negative-control fixture: constrained generic parameter with a nullable annotation.</summary>
    public sealed record NullableSurfaceRecord<T>(T? Value) where T : class;

    /// <summary>Negative-control fixture: unconstrained generic parameter with and without the annotation.</summary>
    public sealed record UnconstrainedSurfaceRecord<T>(T Plain, T? Annotated, List<T?> Items);
}

namespace System.IO
{
    /// <summary>Negative-control fixture: an assembly-owned public extension type in a System namespace.</summary>
    public static class SurfaceProbeExtensions
    {
        public static void ProbeExtension(this string value)
        {
        }
    }
}
