using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using static PhotoAtomic.IndentedStrings.IndentedInterpolatedStringHandler;

namespace PhotoAtomic.Clooney.Generation;

/// <summary>
/// Incremental source generator that creates Clone() extension methods
/// for classes marked with [Clonable] attribute and all reachable types.
/// </summary>
[Generator]
public class ClonableGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor ObjectWithoutKnownType = new(
        id: "PACLOON001",
        title: "Uncloneable object/dynamic property",
        messageFormat: "Clonable class '{0}' has property '{1}' of type '{2}' without [KnownType]; cannot generate reliable clone",
        category: "PhotoAtomic.Clooney",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnassignablePropertyWarning = new(
        id: "PACLOON002",
        title: "Property will not be cloned",
        messageFormat: "Clonable class '{0}' has property '{1}' without a public or helper setter; it will not be cloned. Make the class partial to enable helper-based cloning.",
        category: "PhotoAtomic.Clooney",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var roots = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "PhotoAtomic.Clooney.ClonableAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => GetClassInfo(ctx, ct))
            .Where(static x => x is not null);

        var compilationAndRoots = context.CompilationProvider.Combine(roots.Collect());

        context.RegisterSourceOutput(compilationAndRoots, (spc, data) =>
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
                var code = GenerateCloneExtensions(classInfo, reachable, rootInfos);
                var namespacePart = classInfo.Namespace.Replace('.', '_');
                var hintName = $"{namespacePart}_{classInfo.ClassName}.Clone.g.cs";
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

        return BuildClassInfo(classSymbol, excludedProps, isRoot: true);
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
        var queue = new Queue<(INamedTypeSymbol symbol, HashSet<string> excludes, bool isRoot)>();

        foreach (var root in roots)
        {
            queue.Enqueue((root.Symbol, root.ExcludedProperties, isRoot: true));
        }

        while (queue.Count > 0)
        {
            var (symbol, excludes, isRoot) = queue.Dequeue();
            if (reachable.ContainsKey(symbol))
            {
                continue;
            }

            var classInfo = BuildClassInfo(symbol, excludes, isRoot);
            reachable[symbol] = classInfo;

            foreach (var prop in classInfo.Properties)
            {
                var candidate = GetCandidateType(prop);
                if (candidate is INamedTypeSymbol named && ShouldEnqueue(named))
                {
                    queue.Enqueue((named, GetExcludes(named), isRoot: false));
                }

                foreach (var known in prop.KnownTypes)
                {
                    if (known is INamedTypeSymbol knownNamed && ShouldEnqueue(knownNamed))
                    {
                        queue.Enqueue((knownNamed, GetExcludes(knownNamed), isRoot: false));
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
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "PhotoAtomic.Clooney.ClonableAttribute")
            ?.NamedArguments.FirstOrDefault(x => x.Key == "Exclude").Value.Value as string;

        return BuildExcludedProperties(excludeArgument);
    }

    private static ClassInfo BuildClassInfo(INamedTypeSymbol classSymbol, HashSet<string> excludedProps, bool isRoot = false)
    {
        var isPartial = IsPartial(classSymbol);

        // Get all properties including inherited ones
        var allProperties = new List<IPropertySymbol>();
        var current = classSymbol;
        while (current != null && current.SpecialType != SpecialType.System_Object)
        {
            var declaredProps = current.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => p.DeclaredAccessibility == Accessibility.Public)
                .Where(p => p.GetMethod != null && p.GetMethod.DeclaredAccessibility == Accessibility.Public)
                .Where(p => !excludedProps.Contains(p.Name))
                .Where(p => !HasSkipClone(p));
            
            allProperties.AddRange(declaredProps);
            current = current.BaseType;
        }

        // Remove duplicates (in case of overridden properties, keep the most derived one)
        var properties = allProperties
            .GroupBy(p => p.Name)
            .Select(g => g.First())
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
            IsPartial = isPartial,
            IsRoot = isRoot
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
        Dictionary<INamedTypeSymbol, ClassInfo> reachable, List<ClassInfo> allRoots)
    {
        var simpleClone = GenerateSimpleClone(classInfo);
        var trackedClone = GenerateTrackedClone(classInfo, reachable);
        var helperSetter = classInfo.IsPartial && classInfo.Properties.Any(p => p.NeedsHelperSetter)
            ? GenerateHelperSetter(classInfo)
            : null;
        var interfaceImpl = classInfo.IsRoot && classInfo.IsPartial && !classInfo.IsInterface && !classInfo.IsAbstract
            ? GenerateClonableInterface(classInfo, allRoots)
            : null;

        return Indent($$"""
            // <auto-generated/>
            #nullable enable
            using System;
            using System.Linq;
            
            namespace {{classInfo.Namespace}}
            {
                /// <summary>
                /// Extension methods for cloning {{classInfo.ClassName}}
                /// Generated by PhotoAtomic.DeepCloner
                /// </summary>
                public static partial class {{classInfo.ClassName}}Extensions
                {
                    {{simpleClone}}
            
                    {{trackedClone}}
            
                    private static T ThrowUncloneable<T>(string message) => throw new global::System.InvalidOperationException(message);
                }
                {{helperSetter}}{{interfaceImpl}}
            }
            """);
    }

    private static string GenerateSimpleClone(ClassInfo classInfo)
    {
        return Indent($$"""
            /// <summary>
            /// Creates a deep clone of the {{classInfo.ClassName}}.
            /// Uses an internal CloneContext to handle circular references.
            /// </summary>
            public static {{classInfo.FullyQualifiedName}}? Clone(this {{classInfo.FullyQualifiedName}}? source)
            {
                if (source == null) return null;
                return source.Clone(new PhotoAtomic.Clooney.CloneContext());
            }
            """);
    }

    private static string GenerateTrackedClone(ClassInfo classInfo, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        var derivedReachable = GetDerivedReachable(classInfo, reachable).ToList();

        if (classInfo.IsInterface || classInfo.IsAbstract)
        {
            return Indent($$"""
                /// <summary>
                /// Creates a deep clone with reference tracking (supports circular references).
                /// </summary>
                public static {{classInfo.FullyQualifiedName}}? Clone(
                    this {{classInfo.FullyQualifiedName}}? source,
                    PhotoAtomic.Clooney.CloneContext context)
                {
                    return source switch
                    {
                        null => null,
                        {{Indent(derivedReachable.Select(derived =>
                            $"""
                            {derived.FullyQualifiedName} d => {derived.ClassName}Extensions.Clone(d, context),
                            
                            """))}}

                        _ => source
                    };
                }
                """);
        }

        var helperProps = classInfo.Properties.Where(p => p.NeedsHelperSetter).ToList();
        var directProps = classInfo.Properties.Where(p => p.HasPublicSetter && !p.NeedsHelperSetter).ToList();

        var helperSetterCall = helperProps.Any()
            ? Indent($$"""
                
                clone.__DeepClone_Setters({{string.Join(", ", helperProps.Select(p => GenerateCloneExpression(classInfo, p, reachable)))}});
                """)
            : null;

        var derivedChecks = derivedReachable.Any()
            ? Indent($$"""
                
                        return source switch
                        {
                            {{Indent(derivedReachable.Select(derived =>
                                $"""
                                {derived.FullyQualifiedName} d => {derived.ClassName}Extensions.Clone(d, context),
                                
                                """))}}
                            _ => CloneInternal(source, context)
                        };
                    }
                    
                    private static {{classInfo.FullyQualifiedName}}? CloneInternal(
                        {{classInfo.FullyQualifiedName}} source,
                        PhotoAtomic.Clooney.CloneContext context)
                    {
                """)
            : "";

        return Indent($$"""
            /// <summary>
            /// Creates a deep clone with reference tracking (supports circular references).
            /// </summary>
            public static {{classInfo.FullyQualifiedName}}? Clone(
                this {{classInfo.FullyQualifiedName}}? source,
                PhotoAtomic.Clooney.CloneContext context)
            {
                if (source == null) return null;{{derivedChecks}}
            
                return context.GetOrClone(
                    source,
                    shellFactory: () => new {{classInfo.FullyQualifiedName}}(),
                    populateAction: (clone, ctx) =>
                    {
                        {{Indent(directProps.Select(prop =>
                        {
                            var cloneExpr = GenerateCloneExpression(classInfo, prop, reachable);
                            return $"clone.{prop.Name} = {cloneExpr};";
                        }))}}{{helperSetterCall}}
                    });
            }
            """);
    }

    private static IEnumerable<ClassInfo> GetDerivedReachable(ClassInfo baseInfo, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        return reachable.Values
            .Where(ci => !SymbolEqualityComparer.Default.Equals(ci.Symbol, baseInfo.Symbol))
            .Where(ci => IsAssignableTo(ci.Symbol, baseInfo.Symbol))
            .OrderByDescending(ci => GetInheritanceDepth(ci.Symbol))  // Deepest first
            .ThenBy(ci => ci.FullyQualifiedName);  // Then alphabetical for stability
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
                return $"ThrowUncloneable<{prop.Type}>(\"Clonable '{owner.FullyQualifiedName}' property '{prop.Name}' of type '{prop.Type}' must declare [KnownType] to be cloned.\")";
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
                    : "ThrowUncloneable<" + elementType + ">(\"Clonable '" + owner.FullyQualifiedName + "' property '" + prop.Name + "' collection element type '" + (prop.CollectionElementType ?? "unknown") + "' cannot be cloned.\")";

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

        return $"ThrowUncloneable<{prop.Type}>(\"Clonable '{owner.FullyQualifiedName}' property '{prop.Name}' of type '{prop.Type}' cannot be cloned (no generated or existing Clone).\")";
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

    private static string? GenerateHelperSetter(ClassInfo classInfo)
    {
        var helperProps = classInfo.Properties.Where(p => p.NeedsHelperSetter).ToList();
        if (!helperProps.Any()) return null;

        // NOTE: Parameters need commas between them, so we use string.Join here
        var parameters = string.Join(",\n", helperProps.Select(prop =>
            $"{prop.Type} {prop.Name.ToLowerInvariant()}"));

        return Indent($$"""
            
            public partial class {{classInfo.ClassName}}
            {
                internal void __DeepClone_Setters(
                    {{parameters}}
                )
                {
                    {{Indent(helperProps.Select(prop =>
                    {
                        var argName = prop.Name.ToLowerInvariant();
                        return $"this.{prop.Name} = {argName};";
                    }))}}
                }
            }
            """);
    }

    private static string GenerateClonableInterface(ClassInfo classInfo, List<ClassInfo> allRoots)
    {
        // Find all types that derive from this class (excluding itself and abstract/interface types)
        var derivedTypes = FindDerivedTypes(classInfo, allRoots)
            .Where(d => !SymbolEqualityComparer.Default.Equals(d.Symbol, classInfo.Symbol))
            .Where(d => !d.IsAbstract && !d.IsInterface)
            .ToList();
        
        // Generate switch expression if there are derived types
        string cloneBody;
        if (derivedTypes.Count > 0) // Has real derived types
        {
            var switchCases = Indent(derivedTypes.Select(derived =>
                $"""
                {derived.FullyQualifiedName} => {derived.ClassName}Extensions.Clone(({derived.FullyQualifiedName})this),
                
                """));
            
            cloneBody = Indent($$"""
                return this switch
                {
                    {{switchCases}}
                    _ => {{classInfo.ClassName}}Extensions.Clone(this)
                };
                """);
        }
        else
        {
            // Simple case: no derived types
            cloneBody = $"return {classInfo.ClassName}Extensions.Clone(this);";
        }

        return Indent($$"""
            
            public partial class {{classInfo.ClassName}} : PhotoAtomic.Clooney.IClonable<{{classInfo.FullyQualifiedName}}>
            {
                public {{classInfo.FullyQualifiedName}}? Clone()
                {
                    {{cloneBody}}
                }
            }
            """);
    }

    private static List<ClassInfo> FindDerivedTypes(ClassInfo baseClass, List<ClassInfo> allTypes)
    {
        var result = new List<ClassInfo>();
        
        foreach (var type in allTypes)
        {
            if (IsDerivedFrom(type.Symbol, baseClass.Symbol))
            {
                result.Add(type);
            }
        }
        
        // Sort by inheritance depth (deepest first)
        result.Sort((a, b) => GetInheritanceDepth(b.Symbol) - GetInheritanceDepth(a.Symbol));
        
        return result;
    }
    
    private static bool IsDerivedFrom(INamedTypeSymbol derived, INamedTypeSymbol baseType)
    {
        var current = derived;
        while (current != null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
            current = current.BaseType;
        }
        return false;
    }
    
    private static int GetInheritanceDepth(INamedTypeSymbol type)
    {
        int depth = 0;
        var current = type.BaseType;
        while (current != null && current.SpecialType != SpecialType.System_Object)
        {
            depth++;
            current = current.BaseType;
        }
        return depth;
    }
}

