using System.Reflection;
using System.Text;
using static MFTLib.Tests.PublicSurfaceTypeFormatter;

namespace MFTLib.Tests;

/// <summary>
///     Lists, by reflection, every type and member an assembly exposes outside itself: public and
///     protected types at any nesting depth, and every public or protected constructor, method,
///     property, field and event each type declares. Compiler-synthesized members (record
///     equality, <c>Deconstruct</c>, <c>&lt;Clone&gt;$</c>, copy constructors) are part of the
///     surface because a consumer can call them. Each entry is one line: the declaring type, then
///     the kind, modifiers, name, parameters and type, formatted by
///     <see cref="PublicSurfaceTypeFormatter" />. Lines are sorted ordinally.
/// </summary>
internal static class PublicSurfaceEnumerator
{
    const BindingFlags DeclaredMembers = BindingFlags.DeclaredOnly | BindingFlags.Public |
                                         BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    static readonly Type[] ImplicitBaseTypes =
        [typeof(object), typeof(ValueType), typeof(Enum), typeof(MulticastDelegate)];

    /// <summary>The public top-level types of an assembly, whatever namespace they declare.</summary>
    public static IEnumerable<Type> TopLevelTypes(Assembly assembly) =>
        assembly.GetTypes().Where(type => type.IsPublic);

    /// <summary>The sorted surface lines of the given top-level types and everything nested in them.</summary>
    public static string[] Enumerate(IEnumerable<Type> topLevelTypes)
    {
        var lines = new List<string>();
        foreach (var type in topLevelTypes)
        {
            AddType(lines, type);
        }

        return lines.OrderBy(line => line, StringComparer.Ordinal).ToArray();
    }

    static void AddType(List<string> lines, Type type)
    {
        var owner = QualifiedName(type, NullableFlags.Oblivious()) + " :: ";
        lines.Add(owner + TypeDeclaration(type));

        foreach (var constructor in type.GetConstructors(DeclaredMembers))
        {
            if (Accessibility(constructor) is { } access)
            {
                lines.Add($"{owner}constructor {access} ({Parameters(constructor)})");
            }
        }

        var properties = type.GetProperties(DeclaredMembers);
        var events = type.GetEvents(DeclaredMembers);
        var accessorTokens = properties.SelectMany(property => property.GetAccessors(nonPublic: true))
            .Concat(events.SelectMany(Accessors))
            .Select(accessor => accessor.MetadataToken)
            .ToHashSet();

        foreach (var method in type.GetMethods(DeclaredMembers))
        {
            if (Accessibility(method) is { } access && !accessorTokens.Contains(method.MetadataToken))
            {
                var genericParameters = method.GetGenericArguments();
                lines.Add($"{owner}method {access}{MethodModifiers(method)} {method.Name}" +
                          $"{GenericParameterList(genericParameters)}({Parameters(method)})" +
                          $" : {ReturnType(method.ReturnParameter)}{Constraints(genericParameters)}");
            }
        }

        foreach (var property in properties)
        {
            if (PropertyLine(property) is { } line)
            {
                lines.Add(owner + line);
            }
        }

        foreach (var field in type.GetFields(DeclaredMembers))
        {
            if (Accessibility(field) is { } access && !field.IsSpecialName)
            {
                lines.Add($"{owner}field {access}{FieldModifiers(field)} {field.Name}" +
                          $" : {FlowAttributes(field.CustomAttributes)}{Format(field.FieldType, NullableFlags.For(field))}" +
                          (field.IsLiteral ? " = " + Literal(field.GetRawConstantValue()) : string.Empty));
            }
        }

        foreach (var eventInfo in events)
        {
            if (eventInfo.AddMethod is { } add && Accessibility(add) is { } access)
            {
                lines.Add($"{owner}event {access}{MethodModifiers(add)} {eventInfo.Name}" +
                          $" : {Format(eventInfo.EventHandlerType!, NullableFlags.For(eventInfo))}");
            }
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (nested.IsNestedPublic || nested.IsNestedFamily || nested.IsNestedFamORAssem)
            {
                AddType(lines, nested);
            }
        }
    }

    static string? PropertyLine(PropertyInfo property)
    {
        var visible = property.GetAccessors(nonPublic: true)
            .Where(accessor => Accessibility(accessor) is not null)
            .OrderBy(accessor => accessor == property.GetMethod ? 0 : 1)
            .ToArray();
        if (visible.Length == 0)
        {
            return null;
        }

        var access = visible.Select(accessor => Accessibility(accessor)!)
            .OrderBy(AccessibilityRank)
            .First();
        var accessors = visible.Select(accessor =>
        {
            var own = Accessibility(accessor)!;
            var prefix = own == access ? string.Empty : own + " ";
            var verb = accessor == property.GetMethod ? "get" : IsInitOnly(accessor) ? "init" : "set";
            return prefix + verb + ";";
        });

        var required = HasAttribute(property.CustomAttributes, "System.Runtime.CompilerServices.RequiredMemberAttribute")
            ? " required"
            : string.Empty;
        var index = property.GetIndexParameters() is { Length: > 0 } indexParameters
            ? "[" + string.Join(", ", indexParameters.Select(Parameter)) + "]"
            : string.Empty;
        var byReference = property.PropertyType.IsByRef ? ByReferenceReturn(property.GetMethod!.ReturnParameter) : string.Empty;

        return $"property {access}{required}{MethodModifiers(visible[0])} {property.Name}{index}" +
               $" {{ {string.Join(' ', accessors)} }} : {FlowAttributes(property.CustomAttributes)}{byReference}" +
               Format(property.PropertyType, NullableFlags.For(property));
    }

    static IEnumerable<MethodInfo> Accessors(EventInfo eventInfo) =>
        new[] { eventInfo.AddMethod, eventInfo.RemoveMethod, eventInfo.RaiseMethod }
            .Where(accessor => accessor is not null)
            .Select(accessor => accessor!);

    static string? Accessibility(MethodBase method) =>
        method.IsPublic ? "public" : method.IsFamily ? "protected" : method.IsFamilyOrAssembly ? "protected internal" : null;

    static string? Accessibility(FieldInfo field) =>
        field.IsPublic ? "public" : field.IsFamily ? "protected" : field.IsFamilyOrAssembly ? "protected internal" : null;

    static int AccessibilityRank(string access) => access switch
    {
        "public" => 0,
        "protected internal" => 1,
        _ => 2
    };

    static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Any(modifier =>
            modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    static bool HasAttribute(IEnumerable<CustomAttributeData> attributes, string fullName) =>
        attributes.Any(attribute => attribute.AttributeType.FullName == fullName);

    static string TypeDeclaration(Type type)
    {
        var access = type.IsNested
            ? type.IsNestedPublic ? "public" : type.IsNestedFamily ? "protected" : "protected internal"
            : "public";
        var (kind, modifiers) = KindAndModifiers(type);

        var bases = new List<string>();
        if (type.IsEnum)
        {
            bases.Add(Format(Enum.GetUnderlyingType(type)));
        }
        else if (type.BaseType is { } baseType && !ImplicitBaseTypes.Contains(baseType))
        {
            bases.Add(Format(baseType));
        }

        bases.AddRange(type.GetInterfaces()
            .Select(Format)
            .OrderBy(name => name, StringComparer.Ordinal));

        var declaration = new StringBuilder("type ").Append(access);
        foreach (var modifier in modifiers)
        {
            declaration.Append(' ').Append(modifier);
        }

        declaration.Append(' ').Append(kind);
        if (bases.Count > 0)
        {
            declaration.Append(" : ").Append(string.Join(", ", bases));
        }

        var inherited = type.DeclaringType?.GetGenericArguments().Length ?? 0;
        return declaration.Append(Constraints(type.GetGenericArguments()[inherited..])).ToString();
    }

    static (string Kind, List<string> Modifiers) KindAndModifiers(Type type)
    {
        var modifiers = new List<string>();
        if (type.IsInterface)
        {
            return ("interface", modifiers);
        }

        if (type.IsEnum)
        {
            return ("enum", modifiers);
        }

        if (type.IsValueType)
        {
            if (HasAttribute(type.CustomAttributes, "System.Runtime.CompilerServices.IsReadOnlyAttribute"))
            {
                modifiers.Add("readonly");
            }

            if (type.IsByRefLike)
            {
                modifiers.Add("ref");
            }

            return ("struct", modifiers);
        }

        if (type.BaseType == typeof(MulticastDelegate))
        {
            return ("delegate", modifiers);
        }

        if (type is { IsAbstract: true, IsSealed: true })
        {
            modifiers.Add("static");
        }
        else if (type.IsAbstract)
        {
            modifiers.Add("abstract");
        }
        else if (type.IsSealed)
        {
            modifiers.Add("sealed");
        }

        return ("class", modifiers);
    }

    static string MethodModifiers(MethodInfo method)
    {
        var modifiers = new StringBuilder();
        if (method.IsStatic)
        {
            modifiers.Append(" static");
        }

        if (method.IsAbstract)
        {
            modifiers.Append(" abstract");
        }
        else if (method.IsVirtual)
        {
            var overrides = method.GetBaseDefinition().DeclaringType != method.DeclaringType;
            if (overrides)
            {
                modifiers.Append(method.IsFinal ? " sealed override" : " override");
            }
            else if (!method.IsFinal)
            {
                modifiers.Append(" virtual");
            }
        }

        if (HasAttribute(method.CustomAttributes, "System.Runtime.CompilerServices.IsReadOnlyAttribute"))
        {
            modifiers.Append(" readonly");
        }

        return modifiers.ToString();
    }

    static string FieldModifiers(FieldInfo field)
    {
        if (field.IsLiteral)
        {
            return " const";
        }

        var modifiers = field.IsStatic ? " static" : string.Empty;
        return field.IsInitOnly ? modifiers + " readonly" : modifiers;
    }

    static string ReturnType(ParameterInfo returnParameter) =>
        FlowAttributes(returnParameter.CustomAttributes, "return: ") + ByReferenceReturn(returnParameter) +
        Format(returnParameter.ParameterType, NullableFlags.For(returnParameter));

    static string ByReferenceReturn(ParameterInfo returnParameter)
    {
        if (!returnParameter.ParameterType.IsByRef)
        {
            return string.Empty;
        }

        return returnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.InteropServices.InAttribute))
            ? "ref readonly "
            : "ref ";
    }

    static string Parameters(MethodBase method)
    {
        var isExtension = HasAttribute(method.CustomAttributes, "System.Runtime.CompilerServices.ExtensionAttribute");
        return string.Join(", ", method.GetParameters().Select((parameter, position) =>
            (isExtension && position == 0 ? "this " : string.Empty) + Parameter(parameter)));
    }

    static string Parameter(ParameterInfo parameter)
    {
        var text = new StringBuilder(FlowAttributes(parameter.CustomAttributes));
        if (HasAttribute(parameter.CustomAttributes, "System.ParamArrayAttribute"))
        {
            text.Append("params ");
        }

        if (parameter.ParameterType.IsByRef)
        {
            text.Append(parameter.IsOut ? "out "
                : HasAttribute(parameter.CustomAttributes, "System.Runtime.CompilerServices.RequiresLocationAttribute") ? "ref readonly "
                : parameter.IsIn ? "in "
                : "ref ");
        }

        text.Append(Format(parameter.ParameterType, NullableFlags.For(parameter)))
            .Append(' ').Append(parameter.Name);
        if (parameter.HasDefaultValue)
        {
            text.Append(" = ").Append(Literal(parameter.RawDefaultValue));
        }

        return text.ToString();
    }

    static string GenericParameterList(Type[] parameters) =>
        parameters.Length == 0 ? string.Empty : "<" + string.Join(", ", parameters.Select(parameter => parameter.Name)) + ">";

    static string Constraints(Type[] genericParameters)
    {
        var clauses = new StringBuilder();
        foreach (var parameter in genericParameters.Where(parameter => parameter.IsGenericParameter))
        {
            var constraints = ConstraintList(parameter);
            if (constraints.Count > 0)
            {
                clauses.Append(" where ").Append(parameter.Name).Append(" : ").Append(string.Join(", ", constraints));
            }
        }

        return clauses.ToString();
    }

    static List<string> ConstraintList(Type parameter)
    {
        var attributes = parameter.GenericParameterAttributes;
        var constraints = new List<string>();
        var flag = NullableFlags.DeclaredFlag(parameter);
        var typeConstraints = parameter.GetGenericParameterConstraints()
            .Where(constraint => constraint != typeof(ValueType))
            .Select(Format)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
        {
            constraints.Add(flag == 2 ? "class?" : "class");
        }
        else if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
        {
            constraints.Add(HasAttribute(parameter.CustomAttributes, "System.Runtime.CompilerServices.IsUnmanagedAttribute")
                ? "unmanaged"
                : "struct");
        }
        else if (flag == 1 && typeConstraints.Length == 0)
        {
            constraints.Add("notnull");
        }

        constraints.AddRange(typeConstraints);
        if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint) &&
            !attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
        {
            constraints.Add("new()");
        }

        if (attributes.HasFlag(GenericParameterAttributes.AllowByRefLike))
        {
            constraints.Add("allows ref struct");
        }

        return constraints;
    }
}
