using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using static PhotoAtomic.IndentedStrings.IndentedInterpolatedStringHandler;

namespace PhotoAtomic.Clooney.Generation;

/// <summary>
/// Incremental source generator that creates Diff() extension methods
/// for classes marked with [Diffable] attribute and all reachable types.
/// </summary>
[Generator]
public class DiffableGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor ObjectWithoutKnownType = new(
        id: "PADIFF001",
        title: "Undiffable object/dynamic property",
        messageFormat: "Diffable class '{0}' has property '{1}' of type '{2}' without [KnownType]; cannot generate reliable diff.",
        category: "PhotoAtomic.Clooney",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var roots = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "PhotoAtomic.Clooney.DiffableAttribute",
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

            var reachable = BuildReachableTypes(rootInfos, compilation);

            ReportDiagnostics(spc, reachable);

            foreach (var classInfo in reachable.Values)
            {
                var code = GenerateDiffExtensions(classInfo, reachable);
                var hintName = $"{classInfo.Namespace}.{classInfo.ClassName}.Diff.g.cs".Replace('.', '_');
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
            }
        }
    }

    private static Dictionary<INamedTypeSymbol, ClassInfo> BuildReachableTypes(IEnumerable<ClassInfo> roots, Compilation compilation)
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

            // If this is an abstract class or interface, find all derived types in the compilation
            if (symbol.IsAbstract || symbol.TypeKind == TypeKind.Interface)
            {
                var derivedTypes = FindDerivedTypes(symbol, compilation);
                foreach (var derivedType in derivedTypes)
                {
                    if (ShouldEnqueue(derivedType))
                    {
                        queue.Enqueue((derivedType, GetExcludes(derivedType)));
                    }
                }
            }

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

    private static IEnumerable<INamedTypeSymbol> FindDerivedTypes(INamedTypeSymbol baseType, Compilation compilation)
    {
        var derivedTypes = new List<INamedTypeSymbol>();

        // Visit all named types in all assemblies (including source)
        var visitor = new DerivedTypeVisitor(baseType, derivedTypes);
        
        // Visit the main compilation
        compilation.GlobalNamespace.Accept(visitor);

        // Visit referenced assemblies
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assemblySymbol)
            {
                assemblySymbol.GlobalNamespace.Accept(visitor);
            }
        }

        return derivedTypes;
    }

    private class DerivedTypeVisitor : SymbolVisitor
    {
        private readonly INamedTypeSymbol _baseType;
        private readonly List<INamedTypeSymbol> _derivedTypes;

        public DerivedTypeVisitor(INamedTypeSymbol baseType, List<INamedTypeSymbol> derivedTypes)
        {
            _baseType = baseType;
            _derivedTypes = derivedTypes;
        }

        public override void VisitNamespace(INamespaceSymbol symbol)
        {
            foreach (var member in symbol.GetMembers())
            {
                member.Accept(this);
            }
        }

        public override void VisitNamedType(INamedTypeSymbol symbol)
        {
            // Check if this type derives from or implements the base type
            if (symbol.TypeKind == TypeKind.Class || symbol.TypeKind == TypeKind.Struct)
            {
                // Check base class
                var current = symbol.BaseType;
                while (current != null)
                {
                    if (SymbolEqualityComparer.Default.Equals(current, _baseType))
                    {
                        _derivedTypes.Add(symbol);
                        break;
                    }
                    current = current.BaseType;
                }
            }

            // Check interfaces (for both classes and structs)
            if (symbol.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, _baseType)))
            {
                _derivedTypes.Add(symbol);
            }

            // Visit nested types
            foreach (var member in symbol.GetTypeMembers())
            {
                member.Accept(this);
            }
        }
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
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "PhotoAtomic.Clooney.DiffableAttribute")
            ?.NamedArguments.FirstOrDefault(x => x.Key == "Exclude").Value.Value as string;

        return BuildExcludedProperties(excludeArgument);
    }

    private static ClassInfo BuildClassInfo(INamedTypeSymbol classSymbol, HashSet<string> excludedProps)
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
                .Where(p => !HasSkipDiff(p));
            
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
            IsPartial = isPartial
        };
    }

    private static bool HasSkipDiff(IPropertySymbol property)
    {
        return property.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "PhotoAtomic.Clooney.SkipDiffAttribute");
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
            HasCloneMethod = HasDiffMethod(type),
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

    private static bool IsDictionary(ITypeSymbol type)
    {
        return type.AllInterfaces.Any(i =>
            i.Name == "IDictionary" &&
            i.ContainingNamespace.ToDisplayString().StartsWith("System.Collections"));
    }

    private static bool HasDiffMethod(ITypeSymbol type)
    {
        return type.GetMembers("Diff")
            .OfType<IMethodSymbol>()
            .Any(m => m.IsExtensionMethod || m.MethodKind == MethodKind.Ordinary);
    }

    private static string GenerateDiffExtensions(
        ClassInfo classInfo,
        Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        var simpleDiff = GenerateSimpleDiff(classInfo);
        var trackedDiff = GenerateTrackedDiff(classInfo, reachable);

        return Indent($$"""
            // <auto-generated/>
            #nullable enable
            using System;
            using System.Collections.Generic;
            using System.Linq;
            
            namespace {{classInfo.Namespace}}
            {
                /// <summary>
                /// Extension methods for computing differences between {{classInfo.ClassName}} object graphs
                /// Generated by PhotoAtomic.Clooney
                /// </summary>
                public static partial class {{classInfo.ClassName}}DiffExtensions
                {
                    {{simpleDiff}}
            
                    {{trackedDiff}}
            
                    private static T ThrowUndiffable<T>(string message) => throw new global::System.InvalidOperationException(message);
                }
            }
            """);
    }

    private static string GenerateSimpleDiff(ClassInfo classInfo)
    {
        return Indent($$"""
            /// <summary>
            /// Computes differences between two {{classInfo.ClassName}} object graphs.
            /// Returns an enumerable of DifferencePath objects describing each difference found.
            /// Handles circular references and tracks reference structure.
            /// </summary>
            public static global::System.Collections.Generic.IEnumerable<PhotoAtomic.Clooney.DifferencePath> Diff(
                this {{classInfo.FullyQualifiedName}}? source,
                {{classInfo.FullyQualifiedName}}? other)
            {
                // Shortcut: same reference
                if (ReferenceEquals(source, other))
                    yield break;
                
                // Null differences
                if (source == null && other != null)
                {
                    yield return new PhotoAtomic.Clooney.DifferencePath(
                        new PhotoAtomic.Clooney.ValueNode(
                            "root",
                            "{{classInfo.FullyQualifiedName}}",
                            null,
                            other));
                    yield break;
                }
                
                if (source != null && other == null)
                {
                    yield return new PhotoAtomic.Clooney.DifferencePath(
                        new PhotoAtomic.Clooney.ValueNode(
                            "root",
                            "{{classInfo.FullyQualifiedName}}",
                            source,
                            null));
                    yield break;
                }
                
                var context = new PhotoAtomic.Clooney.DiffContext();
                foreach (var diff in source!.Diff(other!, context))
                {
                    yield return diff;
                }
            }
            """);
    }

    private static string GenerateTrackedDiff(ClassInfo classInfo, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        var derivedReachable = GetDerivedReachable(classInfo, reachable).ToList();

        if (classInfo.IsInterface || classInfo.IsAbstract)
        {
            var interfaceChecks = string.Join("\n", derivedReachable.Select((derived, index) => Indent($$"""
                if (source is {{derived.FullyQualifiedName}} typed{{index}} && other is {{derived.FullyQualifiedName}} typed{{index}}Other)
                {
                    foreach (var diff in typed{{index}}.Diff(typed{{index}}Other, context))
                        yield return diff;
                    yield break;
                }
                """)));

            return Indent($$"""
                /// <summary>
                /// Computes differences with reference tracking (supports circular references).
                /// </summary>
                public static global::System.Collections.Generic.IEnumerable<PhotoAtomic.Clooney.DifferencePath> Diff(
                    this {{classInfo.FullyQualifiedName}}? source,
                    {{classInfo.FullyQualifiedName}}? other,
                    PhotoAtomic.Clooney.DiffContext context)
                {
                    // Shortcut: same reference
                    if (ReferenceEquals(source, other))
                        yield break;
                    
                    // Null differences
                    if (source == null && other != null)
                    {
                        yield return new PhotoAtomic.Clooney.DifferencePath(
                            new PhotoAtomic.Clooney.ValueNode(
                                "{{classInfo.ClassName}}",
                                "{{classInfo.FullyQualifiedName}}",
                                null,
                                other));
                        yield break;
                    }
                    
                    if (source != null && other == null)
                    {
                        yield return new PhotoAtomic.Clooney.DifferencePath(
                            new PhotoAtomic.Clooney.ValueNode(
                                "{{classInfo.ClassName}}",
                                "{{classInfo.FullyQualifiedName}}",
                                source,
                                null));
                        yield break;
                    }
                    
                    // Check if types are different first
                    if (source!.GetType() != other!.GetType())
                    {
                        yield return new PhotoAtomic.Clooney.DifferencePath(
                            new PhotoAtomic.Clooney.ValueNode(
                                "Type",
                                "System.Type",
                                source.GetType().FullName,
                                other.GetType().FullName));
                        yield break;
                    }
                    
                    // Types are the same, try known derived types
                    {{interfaceChecks}}
                    
                    // Fallback: same type but not a known derived type, compare using GetHashCode or base comparison
                    // Since types are the same, they should be comparable
                    if (!source.Equals(other))
                    {
                        yield return new PhotoAtomic.Clooney.DifferencePath(
                            new PhotoAtomic.Clooney.ValueNode(
                                "{{classInfo.ClassName}}",
                                "{{classInfo.FullyQualifiedName}}",
                                source,
                                other));
                    }
                }
                """);
        }

        var derivedChecks = string.Join("\n", derivedReachable.Select((derived, index) => Indent($$"""
            if (source is {{derived.FullyQualifiedName}} typed{{index}} && other is {{derived.FullyQualifiedName}} typed{{index}}Other)
            {
                foreach (var diff in typed{{index}}.Diff(typed{{index}}Other, context))
                    yield return diff;
                yield break;
            }
            """)));

        var propertyDiffs = classInfo.Properties.Select(prop =>
            GeneratePropertyDiffCode(classInfo, prop, reachable));

        return Indent($$"""
            /// <summary>
            /// Computes differences with reference tracking (supports circular references).
            /// </summary>
            public static global::System.Collections.Generic.IEnumerable<PhotoAtomic.Clooney.DifferencePath> Diff(
                this {{classInfo.FullyQualifiedName}}? source,
                {{classInfo.FullyQualifiedName}}? other,
                PhotoAtomic.Clooney.DiffContext context)
            {
                // Shortcut: same reference
                if (ReferenceEquals(source, other))
                    yield break;
                
                // Null differences
                if (source == null && other != null)
                {
                    yield return new PhotoAtomic.Clooney.DifferencePath(
                        new PhotoAtomic.Clooney.ValueNode(
                            "{{classInfo.ClassName}}",
                            "{{classInfo.FullyQualifiedName}}",
                            null,
                            other));
                    yield break;
                }
                
                if (source != null && other == null)
                {
                    yield return new PhotoAtomic.Clooney.DifferencePath(
                        new PhotoAtomic.Clooney.ValueNode(
                            "{{classInfo.ClassName}}",
                            "{{classInfo.FullyQualifiedName}}",
                            source,
                            null));
                    yield break;
                }
                
                {{derivedChecks}}
                
                // Check if this pair was already visited
                var isNew = context.TryRegisterPair(source!, other!, out var structuralDiff);
                
                // If structural difference detected
                if (structuralDiff != null)
                {
                    yield return new PhotoAtomic.Clooney.DifferencePath(
                        new PhotoAtomic.Clooney.CircularReferenceNode("{{classInfo.ClassName}}", structuralDiff));
                    yield break;
                }
                
                // If already visited (circular reference with same structure), skip
                if (!isNew)
                    yield break;
                
                // Compare all properties
                {{Indent(propertyDiffs)}}
            }
            """);
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

    private static string GeneratePropertyDiffCode(ClassInfo owner, PropertyInfo prop, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        var sourceAccess = "source." + prop.Name;
        var otherAccess = "other." + prop.Name;

        // For value types
        if (prop.IsValueType)
        {
            return Indent($$"""
                if (!global::System.Collections.Generic.EqualityComparer<{{prop.Type}}>.Default.Equals({{sourceAccess}}, {{otherAccess}}))
                {
                    yield return new PhotoAtomic.Clooney.DifferencePath(
                        new PhotoAtomic.Clooney.ValueNode(
                            "{{prop.Name}}",
                            "{{prop.Type}}",
                            {{sourceAccess}},
                            {{otherAccess}}));
                }
                """);
        }

        // For strings
        if (prop.TypeSymbol.SpecialType == SpecialType.System_String)
        {
            return Indent($$"""
                if (!string.Equals({{sourceAccess}}, {{otherAccess}}))
                {
                    yield return new PhotoAtomic.Clooney.DifferencePath(
                        new PhotoAtomic.Clooney.ValueNode(
                            "{{prop.Name}}",
                            "string",
                            {{sourceAccess}},
                            {{otherAccess}}));
                }
                """);
        }

        // For object/dynamic types with KnownTypes
        if (IsObjectLike(prop.TypeSymbol))
        {
            if (prop.KnownTypes.Count == 0)
            {
                return $"ThrowUndiffable<int>(\"Diffable '{owner.FullyQualifiedName}' property '{prop.Name}' of type '{prop.Type}' must declare [KnownType] to be diffed.\");";
            }

            return GenerateKnownTypeDiff(prop, reachable);
        }

        // For collections
        if (prop.IsCollection && prop.CollectionElementTypeSymbol != null)
        {
            return GenerateCollectionDiff(owner, prop, reachable);
        }

        // For reference types
        var typeReachable = IsReachable(prop.TypeSymbol, reachable);

        if (typeReachable)
        {
            return Indent($$"""
                // Diff reference property: {{prop.Name}}
                {
                    var refNode = new PhotoAtomic.Clooney.ReferenceNode(
                        "{{prop.Name}}",
                        "{{prop.Type}}",
                        {{sourceAccess}},
                        {{otherAccess}});
                    
                    foreach (var nestedDiff in {{sourceAccess}}.Diff({{otherAccess}}, context))
                    {
                        // Prepend current property to the path
                        var newRoot = new PhotoAtomic.Clooney.ReferenceNode(
                            "{{prop.Name}}",
                            "{{prop.Type}}",
                            {{sourceAccess}},
                            {{otherAccess}});
                        newRoot.Next = nestedDiff.Root;
                        yield return new PhotoAtomic.Clooney.DifferencePath(newRoot, nestedDiff.Leaf);
                    }
                }
                """);
        }

        if (prop.HasCloneMethod) // HasCloneMethod is repurposed here to check for Diff method
        {
            return Indent($$"""
                // Diff using existing Diff method: {{prop.Name}}
                foreach (var nestedDiff in ({{sourceAccess}}?.Diff({{otherAccess}}) ?? global::System.Linq.Enumerable.Empty<PhotoAtomic.Clooney.DifferencePath>()))
                {
                    var newRoot = new PhotoAtomic.Clooney.ReferenceNode(
                        "{{prop.Name}}",
                        "{{prop.Type}}",
                        {{sourceAccess}},
                        {{otherAccess}});
                    newRoot.Next = nestedDiff.Root;
                    yield return new PhotoAtomic.Clooney.DifferencePath(newRoot, nestedDiff.Leaf);
                }
                """);
        }

        return $"ThrowUndiffable<int>(\"Diffable '{owner.FullyQualifiedName}' property '{prop.Name}' of type '{prop.Type}' cannot be diffed (no generated or existing Diff method).\");";
    }

    private static string GenerateKnownTypeDiff(PropertyInfo prop, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        var sourceAccess = "source." + prop.Name;
        var otherAccess = "other." + prop.Name;

        var cases = new List<string>();
        
        foreach (var known in prop.KnownTypes)
        {
            var knownDisplay = known.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var typeReachable = known is INamedTypeSymbol named && IsReachable(named, reachable);

            if (typeReachable)
            {
                cases.Add(Indent($$"""
                    ({{knownDisplay}} typedSource, {{knownDisplay}} typedOther) => typedSource.Diff(typedOther, context)
                    """));
            }
            else if (HasDiffMethod(known))
            {
                cases.Add(Indent($$"""
                    ({{knownDisplay}} typedSource, {{knownDisplay}} typedOther) => typedSource.Diff(typedOther)
                    """));
            }
            else if (known.IsValueType || known.SpecialType == SpecialType.System_String)
            {
                cases.Add(Indent($$"""
                    ({{knownDisplay}} typedSource, {{knownDisplay}} typedOther) => 
                        global::System.Collections.Generic.EqualityComparer<{{knownDisplay}}>.Default.Equals(typedSource, typedOther)
                            ? global::System.Linq.Enumerable.Empty<PhotoAtomic.Clooney.DifferencePath>()
                            : new[] { new PhotoAtomic.Clooney.DifferencePath(new PhotoAtomic.Clooney.ValueNode("{{prop.Name}}", "{{knownDisplay}}", typedSource, typedOther)) }
                    """));
            }
            else
            {
                cases.Add(Indent($$"""
                    ({{knownDisplay}} typedSource, {{knownDisplay}} typedOther) => 
                        global::System.Linq.Enumerable.Empty<PhotoAtomic.Clooney.DifferencePath>()
                    """));
            }
        }

        return Indent($$"""
            // Diff object/dynamic property with KnownTypes: {{prop.Name}}
            {
                var knownTypeDiffs = ({{sourceAccess}}, {{otherAccess}}) switch
                {
                    (null, null) => global::System.Linq.Enumerable.Empty<PhotoAtomic.Clooney.DifferencePath>(),
                    (null, _) => new[] { new PhotoAtomic.Clooney.DifferencePath(new PhotoAtomic.Clooney.ValueNode("{{prop.Name}}", "{{prop.Type}}", null, {{otherAccess}})) },
                    (_, null) => new[] { new PhotoAtomic.Clooney.DifferencePath(new PhotoAtomic.Clooney.ValueNode("{{prop.Name}}", "{{prop.Type}}", {{sourceAccess}}, null)) },
                    {{Indent(cases.Select(c => c + ","))}},
                    _ => new[] { new PhotoAtomic.Clooney.DifferencePath(new PhotoAtomic.Clooney.ValueNode("{{prop.Name}}", "object", {{sourceAccess}}?.GetType().Name, {{otherAccess}}?.GetType().Name)) }
                };
                
                foreach (var diff in knownTypeDiffs)
                    yield return diff;
            }
            """);
    }

    private static string GenerateCollectionDiff(ClassInfo owner, PropertyInfo prop, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        var sourceAccess = "source." + prop.Name;
        var otherAccess = "other." + prop.Name;
        var isDictionary = IsDictionary(prop.TypeSymbol);

        if (isDictionary)
        {
            return GenerateDictionaryDiff(owner, prop, sourceAccess, otherAccess, reachable);
        }

        var elementReachable = IsReachable(prop.CollectionElementTypeSymbol!, reachable);
        var elementHasDiff = HasDiffMethod(prop.CollectionElementTypeSymbol!);
        var elementIsValue = prop.CollectionElementTypeSymbol!.IsValueType;

        return Indent($$"""
            // Diff collection: {{prop.Name}}
            {
                var sourceCol = {{sourceAccess}}?.ToList();
                var otherCol = {{otherAccess}}?.ToList();
                
                if (sourceCol == null && otherCol != null)
                {
                    yield return new PhotoAtomic.Clooney.DifferencePath(
                        new PhotoAtomic.Clooney.ValueNode("{{prop.Name}}", "{{prop.Type}}", null, otherCol));
                }
                else if (sourceCol != null && otherCol == null)
                {
                    yield return new PhotoAtomic.Clooney.DifferencePath(
                        new PhotoAtomic.Clooney.ValueNode("{{prop.Name}}", "{{prop.Type}}", sourceCol, null));
                }
                else if (sourceCol != null && otherCol != null)
                {
                    // Check size difference
                    if (sourceCol.Count != otherCol.Count)
                    {
                        yield return new PhotoAtomic.Clooney.DifferencePath(
                            new PhotoAtomic.Clooney.CollectionSizeNode("{{prop.Name}}", sourceCol.Count, otherCol.Count));
                    }
                    
                    // Compare elements by index
                    int minCount = global::System.Math.Min(sourceCol.Count, otherCol.Count);
                    for (int i = 0; i < minCount; i++)
                    {
                        var sourceElem = sourceCol[i];
                        var otherElem = otherCol[i];
                        
                        {{GenerateElementDiff(prop, "sourceElem", "otherElem", "i", elementReachable, elementHasDiff, elementIsValue, reachable)}}
                    }
                }
            }
            """);
    }

    private static string GenerateDictionaryDiff(ClassInfo owner, PropertyInfo prop, string sourceAccess, string otherAccess, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        // Get key and value types from IDictionary<TKey, TValue>
        var dictionaryInterface = prop.TypeSymbol.AllInterfaces
            .FirstOrDefault(i => i.Name == "IDictionary" && i.TypeArguments.Length == 2);

        if (dictionaryInterface == null)
        {
            return $"// Dictionary diff not supported for {{prop.Name}}";
        }

        var keyType = dictionaryInterface.TypeArguments[0];
        var valueType = dictionaryInterface.TypeArguments[1];
        var keyTypeDisplay = keyType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var valueTypeDisplay = valueType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var valueReachable = valueType is INamedTypeSymbol namedValue && IsReachable(namedValue, reachable);
        var valueHasDiff = HasDiffMethod(valueType);
        var valueIsValue = valueType.IsValueType;

        return Indent($$"""
            // Diff dictionary: {{prop.Name}}
            {
                var sourceDict = {{sourceAccess}};
                var otherDict = {{otherAccess}};
                
                if (sourceDict == null && otherDict != null)
                {
                    yield return new PhotoAtomic.Clooney.DifferencePath(
                        new PhotoAtomic.Clooney.ValueNode("{{prop.Name}}", "{{prop.Type}}", null, otherDict));
                }
                else if (sourceDict != null && otherDict == null)
                {
                    yield return new PhotoAtomic.Clooney.DifferencePath(
                        new PhotoAtomic.Clooney.ValueNode("{{prop.Name}}", "{{prop.Type}}", sourceDict, null));
                }
                else if (sourceDict != null && otherDict != null)
                {
                    // Check size difference
                    if (sourceDict.Count != otherDict.Count)
                    {
                        yield return new PhotoAtomic.Clooney.DifferencePath(
                            new PhotoAtomic.Clooney.CollectionSizeNode("{{prop.Name}}", sourceDict.Count, otherDict.Count));
                    }
                    
                    // Get all keys from both dictionaries
                    var allKeys = new global::System.Collections.Generic.HashSet<{{keyTypeDisplay}}>(sourceDict.Keys);
                    foreach (var key in otherDict.Keys)
                        allKeys.Add(key);
                    
                    // Compare values by key
                    foreach (var key in allKeys)
                    {
                        bool hasSource = sourceDict.TryGetValue(key, out var sourceValue);
                        bool hasOther = otherDict.TryGetValue(key, out var otherValue);
                        
                        if (!hasSource && hasOther)
                        {
                            var elemNode = new PhotoAtomic.Clooney.CollectionElementNode("{{prop.Name}}", key, "{{valueTypeDisplay}}");
                            elemNode.Next = new PhotoAtomic.Clooney.ValueNode($"[{key}]", "{{valueTypeDisplay}}", null, otherValue);
                            yield return new PhotoAtomic.Clooney.DifferencePath(elemNode, elemNode.GetLeaf());
                        }
                        else if (hasSource && !hasOther)
                        {
                            var elemNode = new PhotoAtomic.Clooney.CollectionElementNode("{{prop.Name}}", key, "{{valueTypeDisplay}}");
                            elemNode.Next = new PhotoAtomic.Clooney.ValueNode($"[{key}]", "{{valueTypeDisplay}}", sourceValue, null);
                            yield return new PhotoAtomic.Clooney.DifferencePath(elemNode, elemNode.GetLeaf());
                        }
                        else if (hasSource && hasOther)
                        {
                            {{GenerateDictionaryValueDiff("sourceValue", "otherValue", "key", valueReachable, valueHasDiff, valueIsValue, valueTypeDisplay, reachable)}}
                        }
                    }
                }
            }
            """);
    }

    private static string GenerateDictionaryValueDiff(
        string sourceVar,
        string otherVar,
        string keyVar,
        bool valueReachable,
        bool valueHasDiff,
        bool valueIsValue,
        string valueTypeDisplay,
        Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        if (valueReachable)
        {
            return Indent($$"""
                foreach (var nestedDiff in {{sourceVar}}.Diff({{otherVar}}, context))
                {
                    var elemNode = new PhotoAtomic.Clooney.CollectionElementNode("{{valueTypeDisplay}}", {{keyVar}}, "{{valueTypeDisplay}}");
                    elemNode.Next = nestedDiff.Root;
                    yield return new PhotoAtomic.Clooney.DifferencePath(elemNode, nestedDiff.Leaf);
                }
                """);
        }

        if (valueHasDiff)
        {
            return Indent($$"""
                foreach (var nestedDiff in {{sourceVar}}.Diff({{otherVar}}))
                {
                    var elemNode = new PhotoAtomic.Clooney.CollectionElementNode("{{valueTypeDisplay}}", {{keyVar}}, "{{valueTypeDisplay}}");
                    elemNode.Next = nestedDiff.Root;
                    yield return new PhotoAtomic.Clooney.DifferencePath(elemNode, nestedDiff.Leaf);
                }
                """);
        }

        if (valueIsValue)
        {
            return Indent($$"""
                if (!global::System.Collections.Generic.EqualityComparer<{{valueTypeDisplay}}>.Default.Equals({{sourceVar}}, {{otherVar}}))
                {
                    var elemNode = new PhotoAtomic.Clooney.CollectionElementNode("{{valueTypeDisplay}}", {{keyVar}}, "{{valueTypeDisplay}}");
                    elemNode.Next = new PhotoAtomic.Clooney.ValueNode($"[{{{keyVar}}}]", "{{valueTypeDisplay}}", {{sourceVar}}, {{otherVar}});
                    yield return new PhotoAtomic.Clooney.DifferencePath(elemNode, elemNode.GetLeaf());
                }
                """);
        }

        return Indent($$"""
            if (!object.Equals({{sourceVar}}, {{otherVar}}))
            {
                var elemNode = new PhotoAtomic.Clooney.CollectionElementNode("{{valueTypeDisplay}}", {{keyVar}}, "{{valueTypeDisplay}}");
                elemNode.Next = new PhotoAtomic.Clooney.ValueNode($"[{{{keyVar}}}]", "{{valueTypeDisplay}}", {{sourceVar}}, {{otherVar}});
                yield return new PhotoAtomic.Clooney.DifferencePath(elemNode, elemNode.GetLeaf());
            }
            """);
    }

    private static string GenerateElementDiff(
        PropertyInfo prop,
        string sourceVar,
        string otherVar,
        string indexVar,
        bool elementReachable,
        bool elementHasDiff,
        bool elementIsValue,
        Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        if (elementReachable)
        {
            return Indent($$"""
                foreach (var nestedDiff in {{sourceVar}}.Diff({{otherVar}}, context))
                {
                    var elemNode = new PhotoAtomic.Clooney.CollectionElementNode("{{prop.Name}}", {{indexVar}}, "{{prop.CollectionElementType}}");
                    elemNode.Next = nestedDiff.Root;
                    yield return new PhotoAtomic.Clooney.DifferencePath(elemNode, nestedDiff.Leaf);
                }
                """);
        }

        if (elementHasDiff)
        {
            return Indent($$"""
                foreach (var nestedDiff in {{sourceVar}}.Diff({{otherVar}}))
                {
                    var elemNode = new PhotoAtomic.Clooney.CollectionElementNode("{{prop.Name}}", {{indexVar}}, "{{prop.CollectionElementType}}");
                    elemNode.Next = nestedDiff.Root;
                    yield return new PhotoAtomic.Clooney.DifferencePath(elemNode, nestedDiff.Leaf);
                }
                """);
        }

        if (elementIsValue)
        {
            return Indent($$"""
                if (!global::System.Collections.Generic.EqualityComparer<{{prop.CollectionElementType}}>.Default.Equals({{sourceVar}}, {{otherVar}}))
                {
                    var elemNode = new PhotoAtomic.Clooney.CollectionElementNode("{{prop.Name}}", {{indexVar}}, "{{prop.CollectionElementType}}");
                    elemNode.Next = new PhotoAtomic.Clooney.ValueNode($"[{{{indexVar}}}]", "{{prop.CollectionElementType}}", {{sourceVar}}, {{otherVar}});
                    yield return new PhotoAtomic.Clooney.DifferencePath(elemNode, elemNode.GetLeaf());
                }
                """);
        }

        if (prop.CollectionElementTypeSymbol?.SpecialType == SpecialType.System_String)
        {
            return Indent($$"""
                if (!string.Equals({{sourceVar}}, {{otherVar}}))
                {
                    var elemNode = new PhotoAtomic.Clooney.CollectionElementNode("{{prop.Name}}", {{indexVar}}, "string");
                    elemNode.Next = new PhotoAtomic.Clooney.ValueNode($"[{{{indexVar}}}]", "string", {{sourceVar}}, {{otherVar}});
                    yield return new PhotoAtomic.Clooney.DifferencePath(elemNode, elemNode.GetLeaf());
                }
                """);
        }

        return Indent($$"""
            if (!object.Equals({{sourceVar}}, {{otherVar}}))
            {
                var elemNode = new PhotoAtomic.Clooney.CollectionElementNode("{{prop.Name}}", {{indexVar}}, "{{prop.CollectionElementType ?? "object"}}");
                elemNode.Next = new PhotoAtomic.Clooney.ValueNode($"[{{{indexVar}}}]", "{{prop.CollectionElementType ?? "object"}}", {{sourceVar}}, {{otherVar}});
                yield return new PhotoAtomic.Clooney.DifferencePath(elemNode, elemNode.GetLeaf());
            }
            """);
    }

    private static bool IsReachable(ITypeSymbol type, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        return type is INamedTypeSymbol named && reachable.ContainsKey(named);
    }
}
