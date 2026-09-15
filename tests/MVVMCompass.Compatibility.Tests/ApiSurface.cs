using System.Globalization;
using System.Reflection;

namespace MVVMCompass.ApiContracts;

// Captures the public API contract for tests. Library runtime code does not use this reflection.
internal static class ApiSurface
{
    internal static string[] Capture(Assembly assembly)
    {
        var nullable = new NullabilityInfoContext();
        List<string> lines = [];
        foreach (var type in assembly.GetExportedTypes())
        {
            var owner = Name(type);
            lines.Add($"type {owner} {type.Attributes}; base={Name(type.BaseType)}; interfaces={string.Join(',', type.GetInterfaces().Where(t => t.IsVisible).Select(Name).Order(StringComparer.Ordinal))}; {Constraints(type.GetGenericArguments())}; {Attributes(type.CustomAttributes)}");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(flags).Where(Visible))
                lines.Add($"{owner}: method {method.Name} {method.Attributes}; {Constraints(method.GetGenericArguments())}; ({string.Join(';', method.GetParameters().Select(Parameter))}) -> {Parameter(method.ReturnParameter)}; {Attributes(method.CustomAttributes)}");
            foreach (var constructor in type.GetConstructors(flags).Where(Visible))
                lines.Add($"{owner}: ctor {constructor.Attributes} ({string.Join(';', constructor.GetParameters().Select(Parameter))}); {Attributes(constructor.CustomAttributes)}");
            foreach (var property in type.GetProperties(flags).Where(p => Visible(p.GetMethod) || Visible(p.SetMethod)))
                lines.Add($"{owner}: property {property.Name}:{Name(property.PropertyType)} {Nullability(nullable.Create(property))}; {Attributes(property.CustomAttributes)}");
            foreach (var field in type.GetFields(flags).Where(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly))
                lines.Add($"{owner}: field {field.Name}:{Name(field.FieldType)} {field.Attributes} {Nullability(nullable.Create(field))}; value={(field.IsLiteral ? Value(field.GetRawConstantValue()) : "none")}; {Attributes(field.CustomAttributes)}");
            foreach (var ev in type.GetEvents(flags).Where(e => Visible(e.AddMethod)))
                lines.Add($"{owner}: event {ev.Name}:{Name(ev.EventHandlerType)} {Nullability(nullable.Create(ev))}; {Attributes(ev.CustomAttributes)}");
        }
        return lines.Order(StringComparer.Ordinal).ToArray();

        string Parameter(ParameterInfo parameter) => $"{parameter.Name}:{Name(parameter.ParameterType)} {parameter.Attributes} {Nullability(nullable.Create(parameter))}; default={(parameter.HasDefaultValue ? Value(parameter.RawDefaultValue) : "none")}; required={string.Join(',', parameter.GetRequiredCustomModifiers().Select(Name))}; optional={string.Join(',', parameter.GetOptionalCustomModifiers().Select(Name))}; {Attributes(parameter.CustomAttributes)}";
    }

    private static bool Visible(MethodBase? method) => method != null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);
    private static string Name(Type? type) => type == null ? "none" : type.IsGenericParameter ? $"!{type.GenericParameterPosition}:{type.Name}"
        : type.IsByRef ? Name(type.GetElementType()) + "&" : type.IsPointer ? Name(type.GetElementType()) + "*"
        : type.IsArray ? Name(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]"
        : type.IsGenericType ? $"{type.GetGenericTypeDefinition().FullName}<{string.Join(',', type.GetGenericArguments().Select(Name))}>" : type.FullName!;

    private static string Constraints(IEnumerable<Type> arguments) => string.Join(';', arguments.Where(t => t.IsGenericParameter)
        .Select(t => $"generic {Name(t)} {t.GenericParameterAttributes} [{string.Join(',', t.GetGenericParameterConstraints().Select(Name).Order(StringComparer.Ordinal))}] {Attributes(t.CustomAttributes, includeNullable: true)}"));
    private static string Nullability(NullabilityInfo info) => $"{info.ReadState}/{info.WriteState}"
        + (info.ElementType is { } element ? $"[{Nullability(element)}]" : "")
        + (info.GenericTypeArguments.Length != 0 ? $"<{string.Join(',', info.GenericTypeArguments.Select(Nullability))}>" : "");

    private static string Attributes(IEnumerable<CustomAttributeData> attributes, bool includeNullable = false) => string.Join(',', attributes
        .Where(a => a.AttributeType.Namespace == "System.Diagnostics.CodeAnalysis"
            || a.AttributeType.Namespace == "System.Runtime.InteropServices"
            || a.AttributeType == typeof(ObsoleteAttribute) || a.AttributeType == typeof(ParamArrayAttribute)
            || a.AttributeType == typeof(FlagsAttribute)
            || a.AttributeType.FullName is "System.Runtime.CompilerServices.ExtensionAttribute"
                or "System.Runtime.CompilerServices.IsReadOnlyAttribute" or "System.Runtime.CompilerServices.RequiredMemberAttribute"
                or "System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute"
            || includeNullable && a.AttributeType.FullName is "System.Runtime.CompilerServices.NullableAttribute")
        .Select(a => $"{Name(a.AttributeType)}({string.Join(',', a.ConstructorArguments.Select(Argument))})"
            + "{" + string.Join(',', a.NamedArguments.Select(n => n.MemberName + "=" + Argument(n.TypedValue)).Order(StringComparer.Ordinal)) + "}")
        .Order(StringComparer.Ordinal));
    private static string Argument(CustomAttributeTypedArgument argument) => argument.Value is IEnumerable<CustomAttributeTypedArgument> array
        ? "[" + string.Join(',', array.Select(Argument)) + "]" : Value(argument.Value);
    private static string Value(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"",
        Type type => Name(type),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null"
    };
}
