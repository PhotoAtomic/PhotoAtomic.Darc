using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;

namespace PhotoAtomic.DeepCloner.Generation;

/// <summary>
/// Incremental source generator that creates Clone() extension methods
/// for classes marked with [DeepCopyable] attribute and all reachable types.
/// </summary>
[Generator]
public class DeepCopyableGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor ObjectWithoutKnownType = new(
        id: "PADEEP001",
        title: "Uncloneable object/dynamic property",
        messageFormat: "DeepCopyable class '{0}' has property '{1}' of type '{2}' without [KnownType]; cannot generate reliable clone.",
        category: "PhotoAtomic.DeepCloner",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnassignablePropertyWarning = new(
        id: "PADEEP002",
        title: "Property will not be cloned",
        messageFormat: "DeepCopyable class '{0}' has property '{1}' without a public or helper setter; it will not be cloned. Make the class partial to enable helper-based cloning.",
        category: "PhotoAtomic.DeepCloner",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var roots = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "PhotoAtomic.DeepCloner.DeepCopyableAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => GetClassInfo(ctx, ct))
            .Where(static x => x is not null);

        var compilationAndRoots = context.CompilationProvider.Combine(roots.Collect());

        context.RegisterSourceOutput(compilationAndRoots, static (spc, data) =>
        {
            var compilation = data.Left;
            var rootInfos = data.Right.Where(x => x is not null).Select(x => x!).ToList();
            if (rootInfos.Count == 0)
            {
                return;
            }

            var reachable = BuildReachableTypes(rootInfos);

            ReportDiagnostics(spc, reachable);

            foreach (var classInfo in reachable.Values)
            {
                var code = GenerateCloneExtensions(classInfo, reachable);
                var hintName = $"{classInfo.Namespace}.{classInfo.ClassName}.Clone.g.cs".Replace('.', '_');
                spc.AddSource(hintName, code);
            }
        });
    }

    private static ClassInfo GetClassInfo(
        GeneratorAttributeSyntaxContext context,
        CancellationToken ct)
    {
        var classSymbol = (INamedTypeSymbol)context.TargetSymbol;
        var attribute = context.Attributes[0];
        var excludedProps = ParseExclude(attribute);

        return BuildClassInfo(classSymbol, excludedProps);
    }

    private static void ReportDiagnostics(SourceProductionContext context, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        foreach (var classInfo in reachable.Values)
        {
            foreach (var prop in classInfo.Properties)
            {
                if (IsObjectLike(prop.TypeSymbol) && prop.KnownTypes.Count == 0)
                {
                    var location = prop.TypeSymbol.Locations.FirstOrDefault();
                    context.ReportDiagnostic(Diagnostic.Create(
                        ObjectWithoutKnownType,
                        location ?? classInfo.Symbol.Locations.FirstOrDefault(),
                        classInfo.FullyQualifiedName,
                        prop.Name,
                        prop.Type));
                }

                if (!prop.IsSettable)
                {
                    var location = prop.TypeSymbol.Locations.FirstOrDefault();
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnassignablePropertyWarning,
                        location ?? classInfo.Symbol.Locations.FirstOrDefault(),
                        classInfo.FullyQualifiedName,
                        prop.Name));
                }
            }
        }
    }

    private static Dictionary<INamedTypeSymbol, ClassInfo> BuildReachableTypes(IEnumerable<ClassInfo> roots)
    {
        var reachable = new Dictionary<INamedTypeSymbol, ClassInfo>(SymbolEqualityComparer.Default);
        var queue = new Queue<(INamedTypeSymbol symbol, HashSet<string> excludes)>();

        foreach (var root in roots)
        {
            queue.Enqueue((root.Symbol, root.ExcludedProperties));
        }

        while (queue.Count > 0)
        {
            var (symbol, excludes) = queue.Dequeue();
            if (reachable.ContainsKey(symbol))
            {
                continue;
            }

            var classInfo = BuildClassInfo(symbol, excludes);
            reachable[symbol] = classInfo;

            foreach (var prop in classInfo.Properties)
            {
                var candidate = GetCandidateType(prop);
                if (candidate is INamedTypeSymbol named && ShouldEnqueue(named))
                {
                    queue.Enqueue((named, GetExcludes(named)));
                }

                foreach (var known in prop.KnownTypes)
                {
                    if (known is INamedTypeSymbol knownNamed && ShouldEnqueue(knownNamed))
                    {
                        queue.Enqueue((knownNamed, GetExcludes(knownNamed)));
                    }
                }
            }
        }

        return reachable;
    }

    private static ITypeSymbol? GetCandidateType(PropertyInfo prop)
    {
        if (prop.IsCollection)
        {
            return prop.CollectionElementTypeSymbol ?? prop.TypeSymbol;
        }

        return prop.TypeSymbol;
    }

    private static bool ShouldEnqueue(INamedTypeSymbol symbol)
    {
        if (symbol.SpecialType == SpecialType.System_String) return false;
        if (symbol.TypeKind == TypeKind.Enum) return false;
        if (symbol.IsValueType) return false;
        if (symbol.TypeKind == TypeKind.Delegate) return false;
        if (symbol.TypeKind == TypeKind.TypeParameter) return false;
        if (IsObjectLike(symbol)) return false;
        return true;
    }

    private static HashSet<string> ParseExclude(AttributeData attribute)
    {
        var excludeValue = attribute.NamedArguments
            .FirstOrDefault(x => x.Key == "Exclude")
            .Value.Value as string;

        return BuildExcludedProperties(excludeValue);
    }

    private static HashSet<string> BuildExcludedProperties(string? exclude)
    {
        var excludedProps = new HashSet<string>(
            exclude?.Split(',')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
            ?? Enumerable.Empty<string>());

        excludedProps.Add("PendingEventsList");
        return excludedProps;
    }

    private static HashSet<string> GetExcludes(INamedTypeSymbol symbol)
    {
        var excludeArgument = symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "PhotoAtomic.DeepCloner.DeepCopyableAttribute")
            ?.NamedArguments.FirstOrDefault(x => x.Key == "Exclude").Value.Value as string;

        return BuildExcludedProperties(excludeArgument);
    }

    private static ClassInfo BuildClassInfo(INamedTypeSymbol classSymbol, HashSet<string> excludedProps)
    {
        var isPartial = IsPartial(classSymbol);

        var properties = classSymbol.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public)
            .Where(p => p.GetMethod != null && p.GetMethod.DeclaredAccessibility == Accessibility.Public)
            .Where(p => !excludedProps.Contains(p.Name))
            .Where(p => !HasSkipClone(p))
            .Select(p => CreatePropertyInfo(p, isPartial))
            .ToList();

        return new ClassInfo
        {
            Symbol = classSymbol,
            Namespace = classSymbol.ContainingNamespace.ToDisplayString(),
            ClassName = classSymbol.Name,
            FullyQualifiedName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Properties = properties,
            ExcludedProperties = new HashSet<string>(excludedProps),
            IsInterface = classSymbol.TypeKind == TypeKind.Interface,
            IsAbstract = classSymbol.IsAbstract,
            IsPartial = isPartial
        };
    }

    private static bool HasSkipClone(IPropertySymbol property)
    {
        return property.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "PhotoAtomic.DeepCloner.SkipCloneAttribute");
    }

    private static bool IsPartial(INamedTypeSymbol symbol)
    {
        return symbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<TypeDeclarationSyntax>()
            .Any(s => s.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));
    }

    private static PropertyInfo CreatePropertyInfo(IPropertySymbol property, bool ownerIsPartial)
    {
        var type = property.Type;
        var isCollection = IsCollection(type);
        var collectionElementType = GetCollectionElementTypeSymbol(type);
        var knownTypes = GetKnownTypes(property);
        var hasSetter = property.SetMethod != null;
        var hasPublicSetter = property.SetMethod?.DeclaredAccessibility == Accessibility.Public;
        var needsHelper = hasSetter && !hasPublicSetter && ownerIsPartial;

        return new PropertyInfo
        {
            Name = property.Name,
            TypeSymbol = type,
            Type = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IsReferenceType = type.IsReferenceType,
            IsValueType = type.IsValueType,
            IsNullable = type.NullableAnnotation == NullableAnnotation.Annotated,
            IsCollection = isCollection,
            HasCloneMethod = HasCloneMethod(type),
            CollectionElementTypeSymbol = collectionElementType,
            CollectionElementType = collectionElementType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            KnownTypes = knownTypes,
            HasPublicSetter = hasPublicSetter,
            NeedsHelperSetter = needsHelper
        };
    }

    private static IReadOnlyList<ITypeSymbol> GetKnownTypes(IPropertySymbol property)
    {
        var attrs = property.GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.Serialization.KnownTypeAttribute")
            .ToList();

        var known = new List<ITypeSymbol>();
        foreach (var attr in attrs)
        {
            if (attr.ConstructorArguments.Length > 0)
            {
                var arg = attr.ConstructorArguments[0];
                if (arg.Kind == TypedConstantKind.Type && arg.Value is ITypeSymbol typeSymbol)
                {
                    known.Add(typeSymbol);
                }
            }
        }

        return known;
    }

    private static bool IsCollection(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol) return true;

        return type.AllInterfaces.Any(i =>
            i.Name == "IEnumerable" &&
            i.ContainingNamespace.ToDisplayString().StartsWith("System.Collections"));
    }

    private static bool IsObjectLike(ITypeSymbol type)
    {
        return type.SpecialType == SpecialType.System_Object || type.TypeKind == TypeKind.Dynamic;
    }

    private static ITypeSymbol? GetCollectionElementTypeSymbol(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol arrayType)
        {
            return arrayType.ElementType;
        }

        var enumerableInterface = type.AllInterfaces
            .FirstOrDefault(i => i.Name == "IEnumerable" && i.TypeArguments.Length == 1);

        if (enumerableInterface != null)
        {
            return enumerableInterface.TypeArguments[0];
        }

        return null;
    }

    private static bool HasCloneMethod(ITypeSymbol type)
    {
        return type.GetMembers("Clone")
            .OfType<IMethodSymbol>()
            .Any(m => m.IsExtensionMethod || m.MethodKind == MethodKind.Ordinary);
    }

    private static string GenerateCloneExtensions(
        ClassInfo classInfo,
        Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine();
        sb.AppendLine($"namespace {classInfo.Namespace}");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine($"    /// Extension methods for cloning {classInfo.ClassName}");
        sb.AppendLine("    /// Generated by PhotoAtomic.DeepCloner");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine($"    public static partial class {classInfo.ClassName}Extensions");
        sb.AppendLine("    {");

        GenerateSimpleClone(sb, classInfo);

        sb.AppendLine();
        GenerateTrackedClone(sb, classInfo, reachable);

        sb.AppendLine();
        sb.AppendLine("        private static T ThrowUncloneable<T>(string message) => throw new global::System.InvalidOperationException(message);");

        sb.AppendLine("    }");

        if (classInfo.IsPartial && classInfo.Properties.Any(p => p.NeedsHelperSetter))
        {
            sb.AppendLine();
            GenerateHelperSetter(sb, classInfo);
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void GenerateSimpleClone(StringBuilder sb, ClassInfo classInfo)
    {
        sb.AppendLine("        /// <summary>");
        sb.AppendLine($"        /// Creates a deep clone of the {classInfo.ClassName}.");
        sb.AppendLine("        /// Uses an internal CloneContext to handle circular references.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine($"        public static {classInfo.FullyQualifiedName}? Clone(this {classInfo.FullyQualifiedName}? source)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (source == null) return null;");
        sb.AppendLine();
        sb.AppendLine("            return source.Clone(new PhotoAtomic.DeepCloner.CloneContext());");
        sb.AppendLine("        }");
    }

    private static void GenerateTrackedClone(StringBuilder sb, ClassInfo classInfo, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Creates a deep clone with reference tracking (supports circular references).");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine($"        public static {classInfo.FullyQualifiedName}? Clone(");
        sb.AppendLine($"            this {classInfo.FullyQualifiedName}? source,");
        sb.AppendLine("            PhotoAtomic.DeepCloner.CloneContext context)");
        sb.AppendLine("        {");
        var derivedReachable = GetDerivedReachable(classInfo, reachable).ToList();

        if (classInfo.IsInterface || classInfo.IsAbstract)
        {
            sb.AppendLine("            return source switch");
            sb.AppendLine("            {");
            sb.AppendLine("                null => null,");

            foreach (var derived in derivedReachable)
            {
                var derivedName = derived.FullyQualifiedName;
                sb.AppendLine($"                {derivedName} typed => typed.Clone(context),");
            }

            sb.AppendLine("                _ => source");
            sb.AppendLine("            };");
            sb.AppendLine("        }");
            return;
        }

        sb.AppendLine("            if (source == null) return null;");
        if (derivedReachable.Any())
        {
            foreach (var derived in derivedReachable)
            {
                var derivedName = derived.FullyQualifiedName;
                sb.AppendLine($"            if (source is {derivedName} typed) return typed.Clone(context);");
            }
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("            return context.GetOrClone(");
        sb.AppendLine("                source,");
        sb.AppendLine($"                shellFactory: () => new {classInfo.FullyQualifiedName}(),");
        sb.AppendLine("                populateAction: (clone, ctx) =>");
        sb.AppendLine("                {");

        var helperProps = classInfo.Properties.Where(p => p.NeedsHelperSetter).ToList();
        var directProps = classInfo.Properties.Where(p => p.HasPublicSetter && !p.NeedsHelperSetter).ToList();

        foreach (var prop in directProps)
        {
            var cloneExpr = GenerateCloneExpression(classInfo, prop, reachable);
            sb.AppendLine($"                    clone.{prop.Name} = {cloneExpr};");
        }

        if (helperProps.Any())
        {
            var args = helperProps
                .Select(p => GenerateCloneExpression(classInfo, p, reachable))
                .ToList();

            sb.AppendLine($"                    clone.__DeepClone_Setters({string.Join(", ", args)});");
        }

        sb.AppendLine("                });");
        sb.AppendLine("        }");
    }

    private static IEnumerable<ClassInfo> GetDerivedReachable(ClassInfo baseInfo, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        return reachable.Values
            .Where(ci => !SymbolEqualityComparer.Default.Equals(ci.Symbol, baseInfo.Symbol))
            .Where(ci => IsAssignableTo(ci.Symbol, baseInfo.Symbol))
            .OrderBy(ci => ci.FullyQualifiedName);
    }

    private static bool IsAssignableTo(INamedTypeSymbol symbol, INamedTypeSymbol target)
    {
        if (SymbolEqualityComparer.Default.Equals(symbol, target))
        {
            return true;
        }

        if (target.TypeKind == TypeKind.Interface)
        {
            return symbol.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, target));
        }

        var current = symbol.BaseType;
        while (current != null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, target))
            {
                return true;
            }
            current = current.BaseType;
        }

        return false;
    }

    private static string GenerateCloneExpression(ClassInfo owner, PropertyInfo prop, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        var sourceAccess = "source." + prop.Name;

        if (prop.IsValueType)
        {
            return sourceAccess;
        }

        if (prop.TypeSymbol.SpecialType == SpecialType.System_String)
        {
            return sourceAccess;
        }

        if (IsObjectLike(prop.TypeSymbol))
        {
            if (prop.KnownTypes.Count == 0)
            {
                return $"ThrowUncloneable<{prop.Type}>(\"DeepCopyable '{owner.FullyQualifiedName}' property '{prop.Name}' of type '{prop.Type}' must declare [KnownType] to be cloned.\")";
            }

            var cases = new List<string>();
            foreach (var known in prop.KnownTypes)
            {
                var knownDisplay = known.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var cloneExpr = GenerateKnownTypeClone("typed", known, reachable);
                cases.Add($"{knownDisplay} typed => {cloneExpr}");
            }

            cases.Add($"_ => {sourceAccess}");
            return $"{sourceAccess} switch {{ {string.Join(", ", cases)} }}";
        }

        if (prop.IsCollection && prop.CollectionElementTypeSymbol != null)
        {
            var elementReachable = IsReachable(prop.CollectionElementTypeSymbol, reachable);
            var elementHasClone = HasCloneMethod(prop.CollectionElementTypeSymbol);

            var elementType = prop.CollectionElementType ?? "object";
            var selector = elementReachable
                ? "x?.Clone(ctx)"
                : elementHasClone
                    ? "x?.Clone()"
                    : "ThrowUncloneable<" + elementType + ">(\"DeepCopyable '" + owner.FullyQualifiedName + "' property '" + prop.Name + "' collection element type '" + (prop.CollectionElementType ?? "unknown") + "' cannot be cloned.\")";

            return $"{sourceAccess}?.Select(x => {selector}).ToList()";
        }

        var typeReachable = IsReachable(prop.TypeSymbol, reachable);

        if (typeReachable)
        {
            return $"{sourceAccess}?.Clone(ctx)";
        }

        if (prop.HasCloneMethod)
        {
            return $"{sourceAccess}?.Clone()";
        }

        return $"ThrowUncloneable<{prop.Type}>(\"DeepCopyable '{owner.FullyQualifiedName}' property '{prop.Name}' of type '{prop.Type}' cannot be cloned (no generated or existing Clone).\")";
    }

    private static string GenerateKnownTypeClone(string identifier, ITypeSymbol typeSymbol, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        if (typeSymbol is INamedTypeSymbol named && IsReachable(named, reachable))
        {
            return $"{identifier}.Clone(ctx)";
        }

        if (HasCloneMethod(typeSymbol))
        {
            return $"{identifier}.Clone()";
        }

        return identifier;
    }

    private static bool IsReachable(ITypeSymbol type, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        return type is INamedTypeSymbol named && reachable.ContainsKey(named);
    }

    private static void GenerateHelperSetter(StringBuilder sb, ClassInfo classInfo)
    {
        var helperProps = classInfo.Properties.Where(p => p.NeedsHelperSetter).ToList();
        if (!helperProps.Any()) return;

        sb.AppendLine($"    public partial class {classInfo.ClassName}");
        sb.AppendLine("    {");
        sb.AppendLine("        internal void __DeepClone_Setters(");

        for (int i = 0; i < helperProps.Count; i++)
        {
            var prop = helperProps[i];
            var comma = i < helperProps.Count - 1 ? "," : "";
            sb.AppendLine($"            {prop.Type} {prop.Name.ToLowerInvariant()}{comma}");
        }

        sb.AppendLine("        )");
        sb.AppendLine("        {");
        foreach (var prop in helperProps)
        {
            var argName = prop.Name.ToLowerInvariant();
            sb.AppendLine($"            this.{prop.Name} = {argName};");
        }
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }
}
