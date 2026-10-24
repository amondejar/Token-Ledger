using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using static TokenLedger.Tests.Harness;

namespace TokenLedger.Tests;

/// <summary>
/// The package's public API is recorded in PublicAPI.txt, one member per line. A change to the API fails this
/// test until the file is updated (TOKENLEDGER_UPDATE_PUBLIC_API=1), so every API change shows up in the pull
/// request diff. From 1.0, a removed or changed line is a breaking change and needs a major version.
/// </summary>
internal static class PublicApiTests
{
    public static void Register() => Check(typeof(Ledger).Assembly, "src/TokenLedger/PublicAPI.txt");

    private static void Check(Assembly assembly, string relativePath) =>
        Test($"Public API: {assembly.GetName().Name} matches {relativePath}", () =>
        {
            var file = Path.Combine(RepositoryRoot(), relativePath);
            var current = PublicApi.Describe(assembly);
            var update = Environment.GetEnvironmentVariable("TOKENLEDGER_UPDATE_PUBLIC_API") is "1" or "true";

            if (!File.Exists(file) || update)
            {
                if (!File.Exists(file) || File.ReadAllText(file) != current) File.WriteAllText(file, current);
                if (update) return;
                throw new AssertionException($"{relativePath} was missing and has been created. Review it and commit it.");
            }

            var recorded = File.ReadAllText(file).ReplaceLineEndings("\n");
            if (recorded == current) return;

            var before = recorded.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var after = current.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var report = new StringBuilder($"The public API of {assembly.GetName().Name} changed:\n");
            foreach (var line in before.Except(after).Order(StringComparer.Ordinal)) report.Append("  - ").Append(line).Append('\n');
            foreach (var line in after.Except(before).Order(StringComparer.Ordinal)) report.Append("  + ").Append(line).Append('\n');
            report.Append("Removed (-) or changed lines break callers. If the change is intended, run the tests with TOKENLEDGER_UPDATE_PUBLIC_API=1 and commit the file.");
            throw new AssertionException(report.ToString());
        });

    internal static string RepositoryRoot([CallerFilePath] string here = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(here) ?? "."); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TokenLedger.slnx"))) return directory.FullName;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TokenLedger.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("TokenLedger.slnx not found above the test project.");
    }
}

/// <summary>Writes an assembly's public surface as sorted, C#-like lines.</summary>
internal static class PublicApi
{
    private static readonly NullabilityInfoContext Nullability = new();

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static string Describe(Assembly assembly)
    {
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes().Where(t => !t.Name.StartsWith('<')))
        {
            var name = TypeName(type);
            lines.Add(TypeDeclaration(type, name));
            if (type.IsEnum)
            {
                lines.AddRange(Enum.GetNames(type).Select(v => $"{name}.{v}"));
                continue;
            }

            if (typeof(Delegate).IsAssignableFrom(type)) continue;

            foreach (var member in type.GetMembers(Declared).Where(Visible))
            {
                if (Member(member, name) is { } line) lines.Add(Marks(member) + line);
            }
        }

        return string.Join("\n", lines.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) + "\n";
    }

    private static bool Visible(MemberInfo member) => member switch
    {
        MethodBase m => (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly) && !m.Name.StartsWith('<'),
        FieldInfo f => (f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly) && !f.Name.StartsWith('<'),
        PropertyInfo p => p.GetAccessors(nonPublic: true).Any(a => a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly),
        EventInfo e => e.AddMethod is { } add && (add.IsPublic || add.IsFamily),
        Type => false, // nested types are listed on their own
        _ => false,
    };

    private static string Marks(MemberInfo member)
    {
        var marks = new StringBuilder();
        // The compiler marks constructors of types with required members [Obsolete] for old compilers: not a real deprecation.
        if (member.GetCustomAttribute<ObsoleteAttribute>() is not null && member.GetCustomAttribute<CompilerFeatureRequiredAttribute>() is null)
            marks.Append("[Obsolete] ");
        if (member.GetCustomAttribute<EditorBrowsableAttribute>()?.State == EditorBrowsableState.Never) marks.Append("[EditorBrowsable(Never)] ");
        return marks.ToString();
    }

    private static string TypeDeclaration(Type type, string name)
    {
        var kind = type switch
        {
            { IsInterface: true } => "interface",
            { IsEnum: true } => "enum",
            { IsValueType: true } => "struct",
            _ when typeof(Delegate).IsAssignableFrom(type) => "delegate",
            { IsAbstract: true, IsSealed: true } => "static class",
            { IsAbstract: true } => "abstract class",
            { IsSealed: true } => "sealed class",
            _ => "class",
        };

        var bases = new List<string>();
        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum)
            && baseType != typeof(MulticastDelegate))
            bases.Add(TypeName(baseType));
        bases.AddRange(type.GetInterfaces()
            .Where(i => i.IsPublic || i.IsNestedPublic)
            .Where(i => type.BaseType is null || !i.IsAssignableFrom(type.BaseType))
            .Select(TypeName)
            .Order(StringComparer.Ordinal));

        var marks = type.GetCustomAttribute<EditorBrowsableAttribute>()?.State == EditorBrowsableState.Never ? "[EditorBrowsable(Never)] " : "";
        if (type.GetCustomAttribute<ObsoleteAttribute>() is not null) marks = "[Obsolete] " + marks;
        return $"{marks}{kind} {name}{(bases.Count > 0 ? " : " + string.Join(", ", bases) : "")}{Constraints(type.IsGenericTypeDefinition ? type.GetGenericArguments() : [])}";
    }

    private static string? Member(MemberInfo member, string owner)
    {
        switch (member)
        {
            case ConstructorInfo c:
                return $"{Access(c)}{(c.IsStatic ? "static " : "")}{owner}({Parameters(c)})";

            case MethodInfo m when m.IsSpecialName && !m.Name.StartsWith("op_", StringComparison.Ordinal):
                return null; // property and event accessors

            case MethodInfo m:
                var generic = m.IsGenericMethodDefinition ? $"<{string.Join(", ", m.GetGenericArguments().Select(a => a.Name))}>" : "";
                return $"{Access(m)}{Modifiers(m)}{TypeName(m.ReturnType, Nullability.Create(m.ReturnParameter))} {owner}.{m.Name}{generic}({Parameters(m)}){Constraints(m.IsGenericMethodDefinition ? m.GetGenericArguments() : [])}";

            case PropertyInfo p:
                var accessors = string.Join(" ", new[] { p.GetMethod, p.SetMethod }
                    .Where(a => a is not null && (a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly))
                    .Select(a => a == p.GetMethod ? "get;" : IsInit(p.SetMethod!) ? "init;" : "set;"));
                var indexer = p.GetIndexParameters() is { Length: > 0 } ip ? $"[{string.Join(", ", ip.Select(x => $"{TypeName(x.ParameterType, Nullability.Create(x))} {x.Name}"))}]" : "";
                var accessor = p.GetMethod ?? p.SetMethod!;
                var required = p.IsDefined(typeof(RequiredMemberAttribute), inherit: false) ? "required " : "";
                return $"{Access(accessor)}{required}{Modifiers(accessor)}{TypeName(p.PropertyType, Nullability.Create(p))} {owner}.{p.Name}{indexer} {{ {accessors} }}";

            case FieldInfo f:
                var constant = f.IsLiteral ? $" = {Literal(f.GetRawConstantValue())}" : "";
                return $"{Access(f)}{(f.IsLiteral ? "const " : f.IsStatic ? "static " : "")}{(f.IsInitOnly ? "readonly " : "")}{TypeName(f.FieldType, Nullability.Create(f))} {owner}.{f.Name}{constant}";

            case EventInfo e:
                return $"public event {TypeName(e.EventHandlerType!)} {owner}.{e.Name}";

            default:
                return null;
        }
    }

    private static bool IsInit(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m == typeof(IsExternalInit));

    private static string Access(MethodBase m) => m.IsPublic ? "public " : "protected ";

    private static string Access(FieldInfo f) => f.IsPublic ? "public " : "protected ";

    private static string Modifiers(MethodInfo m)
    {
        if (m.IsStatic) return "static ";
        if (m.IsAbstract) return "abstract ";
        if (m.IsVirtual && !m.IsFinal) return m.GetBaseDefinition() != m ? "override " : "virtual ";
        if (m.IsVirtual && m.GetBaseDefinition() != m) return "sealed override ";
        return "";
    }

    private static string Parameters(MethodBase method)
    {
        var isExtension = method.IsDefined(typeof(ExtensionAttribute), inherit: false);
        return string.Join(", ", method.GetParameters().Select((p, i) =>
        {
            var prefix = i == 0 && isExtension ? "this " : "";
            if (p.IsDefined(typeof(ParamArrayAttribute), inherit: false)) prefix += "params ";
            var type = p.ParameterType;
            if (type.IsByRef)
            {
                prefix += p.IsOut ? "out " : p.IsIn ? "in " : "ref ";
                type = type.GetElementType()!;
            }

            var defaultValue = !p.HasDefaultValue ? ""
                : p.RawDefaultValue is null && type.IsValueType && Nullable.GetUnderlyingType(type) is null ? " = default"
                : $" = {Literal(p.RawDefaultValue)}";
            return $"{prefix}{TypeName(type, Nullability.Create(p))} {p.Name}{defaultValue}";
        }));
    }

    private static string Constraints(Type[] parameters)
    {
        var clauses = new List<string>();
        foreach (var p in parameters)
        {
            var parts = new List<string>();
            var attributes = p.GenericParameterAttributes;
            if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint)) parts.Add("class");
            if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)) parts.Add("struct");
            parts.AddRange(p.GetGenericParameterConstraints().Where(c => c != typeof(ValueType)).Select(TypeName));
            if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint) && !attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
                parts.Add("new()");
            if (parts.Count > 0) clauses.Add($" where {p.Name} : {string.Join(", ", parts)}");
        }

        return string.Concat(clauses);
    }

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        char c => $"'{c}'",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };

    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(void)] = "void", [typeof(object)] = "object", [typeof(string)] = "string", [typeof(bool)] = "bool",
        [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(short)] = "short", [typeof(ushort)] = "ushort",
        [typeof(int)] = "int", [typeof(uint)] = "uint", [typeof(long)] = "long", [typeof(ulong)] = "ulong",
        [typeof(float)] = "float", [typeof(double)] = "double", [typeof(decimal)] = "decimal", [typeof(char)] = "char",
    };

    /// <summary>The C# name, with <c>?</c> on reference types that are annotated nullable.</summary>
    private static string TypeName(Type type, NullabilityInfo? nullability)
    {
        var name = TypeName(type, nullability?.GenericTypeArguments, nullability?.ElementType);
        // Type parameters are left bare: their annotation depends on the type argument.
        return nullability is { ReadState: NullabilityState.Nullable } && !type.IsValueType && !type.IsGenericParameter && !name.EndsWith('?')
            ? name + "?"
            : name;
    }

    private static string TypeName(Type type) => TypeName(type, null, null);

    private static string TypeName(Type type, NullabilityInfo[]? arguments, NullabilityInfo? element)
    {
        if (Keywords.TryGetValue(type, out var keyword)) return keyword;
        if (type.IsGenericParameter) return type.Name;
        if (type.IsArray) return TypeName(type.GetElementType()!, element) + "[]";
        if (type.IsByRef) return TypeName(type.GetElementType()!, element);
        if (Nullable.GetUnderlyingType(type) is { } underlying) return TypeName(underlying) + "?";

        var name = type.IsNested ? $"{TypeName(type.DeclaringType!)}.{type.Name}" : $"{type.Namespace}.{type.Name}";
        if (!type.IsGenericType) return name;

        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        // A nested type's arguments include its parent's; show only its own.
        var all = type.GetGenericArguments();
        var skip = type.IsNested ? type.DeclaringType!.GetGenericArguments().Length : 0;
        var own = all.Skip(skip).Select((t, i) => arguments is { } a && a.Length == all.Length ? TypeName(t, a[i + skip]) : TypeName(t)).ToArray();
        return own.Length == 0 ? name : $"{name}<{string.Join(", ", own)}>";
    }
}
