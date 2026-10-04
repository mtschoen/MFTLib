using System.Globalization;
using System.Reflection;
using System.Text;

namespace MFTLib.Tests;

/// <summary>
///     Formats types for <see cref="PublicSurfaceEnumerator" />: namespace, every nesting level
///     with that level's own generic arguments (so <c>Outer&lt;T&gt;.Nested&lt;U&gt;</c> never
///     collapses to <c>Outer&lt;T, U&gt;</c>), and a <c>?</c> wherever the compiler's nullable
///     metadata marks a position annotated, including generic parameters and generic arguments.
///     Formatting is invariant so the output is identical on every platform.
/// </summary>
internal static class PublicSurfaceTypeFormatter
{
    const byte Annotated = 2;

    static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(void)] = "void",
        [typeof(object)] = "object",
        [typeof(string)] = "string",
        [typeof(bool)] = "bool",
        [typeof(char)] = "char",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal"
    };

    /// <summary>Formats a type where no nullable metadata applies (base types, interfaces, constraints).</summary>
    public static string Format(Type type) => Format(type, NullableFlags.Oblivious());

    /// <summary>
    ///     Formats a type, consuming nullable flags in the compiler's pre-order: one flag for each
    ///     reference type, array, type parameter and generic value type; none for a non-generic
    ///     value type, for <see cref="Nullable{T}" /> itself, or for a by-reference wrapper.
    /// </summary>
    public static string Format(Type type, NullableFlags flags)
    {
        if (type.IsByRef)
        {
            return Format(type.GetElementType()!, flags);
        }

        if (type.IsPointer)
        {
            return Format(type.GetElementType()!, flags) + "*";
        }

        if (type.IsArray)
        {
            var arrayMarker = Marker(flags.Next());
            return Format(type.GetElementType()!, flags) + "[" + new string(',', type.GetArrayRank() - 1) + "]" + arrayMarker;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Format(underlying, flags) + "?";
        }

        if (type.IsGenericParameter)
        {
            return type.Name + Marker(flags.Next());
        }

        if (type.IsValueType && !type.IsGenericType)
        {
            return Keywords.TryGetValue(type, out var valueKeyword) ? valueKeyword : QualifiedName(type, flags);
        }

        var flag = flags.Next();
        var marker = type.IsValueType ? string.Empty : Marker(flag);
        return (Keywords.TryGetValue(type, out var keyword) ? keyword : QualifiedName(type, flags)) + marker;
    }

    /// <summary>
    ///     The namespace-qualified name with every nesting level's own generic arguments, each
    ///     argument formatted in order with the given flags.
    /// </summary>
    public static string QualifiedName(Type type, NullableFlags flags)
    {
        var chain = new List<Type>();
        for (var level = type; level is not null; level = level.DeclaringType)
        {
            chain.Insert(0, level);
        }

        var arguments = type.GetGenericArguments();
        var name = new StringBuilder();
        if (!string.IsNullOrEmpty(chain[0].Namespace))
        {
            name.Append(chain[0].Namespace).Append('.');
        }

        var consumed = 0;
        for (var depth = 0; depth < chain.Count; depth++)
        {
            if (depth > 0)
            {
                name.Append('.');
            }

            var level = chain[depth];
            var tick = level.Name.IndexOf('`');
            name.Append(tick < 0 ? level.Name : level.Name[..tick]);

            var cumulative = depth == chain.Count - 1 ? arguments.Length : level.GetGenericArguments().Length;
            if (cumulative > consumed)
            {
                var own = new List<string>();
                for (var position = consumed; position < cumulative; position++)
                {
                    own.Add(Format(arguments[position], flags));
                }

                name.Append('<').Append(string.Join(", ", own)).Append('>');
                consumed = cumulative;
            }
        }

        return name.ToString();
    }

    /// <summary>
    ///     The nullable flow attributes (<c>MaybeNull</c>, <c>NotNullWhen</c> and the rest of
    ///     <c>System.Diagnostics.CodeAnalysis</c>) on a parameter, return value, property or
    ///     field, each written as a C# attribute followed by a space.
    /// </summary>
    public static string FlowAttributes(IEnumerable<CustomAttributeData> attributes, string target = "") =>
        string.Concat(attributes
            .Where(attribute => attribute.AttributeType.Namespace == "System.Diagnostics.CodeAnalysis" &&
                                attribute.AttributeType.Name.Contains("Null", StringComparison.Ordinal))
            .Select(attribute =>
            {
                var name = attribute.AttributeType.Name;
                name = name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
                var arguments = attribute.ConstructorArguments.Count == 0
                    ? string.Empty
                    : "(" + string.Join(", ", attribute.ConstructorArguments.Select(argument => Literal(argument.Value))) + ")";
                return "[" + target + name + arguments + "] ";
            })
            .OrderBy(text => text, StringComparer.Ordinal));

    public static string Literal(object? value) => value switch
    {
        null => "default",
        string text => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        char character => "'" + character + "'",
        bool boolean => boolean ? "true" : "false",
        IReadOnlyCollection<CustomAttributeTypedArgument> items =>
            "[" + string.Join(", ", items.Select(item => Literal(item.Value))) + "]",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    static string Marker(byte flag) => flag == Annotated ? "?" : string.Empty;
}

/// <summary>
///     The compiler's nullable metadata for one signature position: the position's own
///     <c>NullableAttribute</c> (one byte for every position, or one byte per position in
///     pre-order), else the nearest enclosing <c>NullableContextAttribute</c>, else oblivious.
/// </summary>
internal sealed class NullableFlags
{
    const string NullableAttributeName = "System.Runtime.CompilerServices.NullableAttribute";
    const string NullableContextAttributeName = "System.Runtime.CompilerServices.NullableContextAttribute";

    readonly byte[]? perPosition;
    readonly byte uniform;
    int position;

    NullableFlags(byte[]? perPosition, byte uniform)
    {
        this.perPosition = perPosition;
        this.uniform = uniform;
    }

    public static NullableFlags Oblivious() => new(null, 0);

    public static NullableFlags For(MemberInfo member) => From(member.CustomAttributes, member);

    public static NullableFlags For(ParameterInfo parameter) => From(parameter.CustomAttributes, parameter.Member);

    /// <summary>The nullable annotation the compiler recorded on a generic parameter's declaration.</summary>
    public static byte DeclaredFlag(Type genericParameter) =>
        From(genericParameter.CustomAttributes, (MemberInfo?)genericParameter.DeclaringMethod ?? genericParameter.DeclaringType).Next();

    public byte Next()
    {
        if (perPosition is null)
        {
            return uniform;
        }

        return position < perPosition.Length ? perPosition[position++] : (byte)0;
    }

    static NullableFlags From(IEnumerable<CustomAttributeData> own, MemberInfo? scope)
    {
        var attribute = own.FirstOrDefault(candidate => candidate.AttributeType.FullName == NullableAttributeName);
        if (attribute is { ConstructorArguments.Count: > 0 })
        {
            return attribute.ConstructorArguments[0].Value switch
            {
                byte single => new NullableFlags(null, single),
                IReadOnlyCollection<CustomAttributeTypedArgument> many =>
                    new NullableFlags(many.Select(item => (byte)item.Value!).ToArray(), 0),
                _ => Oblivious()
            };
        }

        for (; scope is not null; scope = scope.DeclaringType)
        {
            var context = scope.CustomAttributes.FirstOrDefault(candidate =>
                candidate.AttributeType.FullName == NullableContextAttributeName);
            if (context is { ConstructorArguments: [{ Value: byte flag }] })
            {
                return new NullableFlags(null, flag);
            }
        }

        return Oblivious();
    }
}
