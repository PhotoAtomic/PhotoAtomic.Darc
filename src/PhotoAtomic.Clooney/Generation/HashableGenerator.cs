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
/// Incremental source generator that creates HashValue() extension methods
/// for classes marked with [Hashable] attribute and all reachable types.
/// </summary>
[Generator]
public class HashableGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor ObjectWithoutKnownType = new(
        id: "PAHASH001",
        title: "Unhashable object/dynamic property",
        messageFormat: "Hashable class '{0}' has property '{1}' of type '{2}' without [KnownType]; cannot generate reliable hash.",
        category: "PhotoAtomic.Clooney",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnhashablePropertyWarning = new(
        id: "PAHASH002",
        title: "Property will not be hashed",
        messageFormat: "Hashable class '{0}' has property '{1}' without a public getter; it will not be included in hash.",
        category: "PhotoAtomic.Clooney",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var roots = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "PhotoAtomic.Clooney.HashableAttribute",
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

            var reachable = BuildReachableTypes(rootInfos, compilation);

            ReportDiagnostics(spc, reachable);

            foreach (var classInfo in reachable.Values)
            {
                var code = GenerateHashExtensions(classInfo, reachable, rootInfos);
                var namespacePart = classInfo.Namespace.Replace('.', '_');
                var hintName = $"{namespacePart}_{classInfo.ClassName}.Hash.g.cs";
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
            }
        }
    }

    private static Dictionary<INamedTypeSymbol, ClassInfo> BuildReachableTypes(IEnumerable<ClassInfo> roots, Compilation compilation)
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

            // If this is an abstract class or interface, find all derived types in the compilation
            if (symbol.IsAbstract || symbol.TypeKind == TypeKind.Interface)
            {
                var derivedTypes = FindDerivedTypes(symbol, compilation);
                foreach (var derivedType in derivedTypes)
                {
                    if (ShouldEnqueue(derivedType))
                    {
                        queue.Enqueue((derivedType, GetExcludes(derivedType), isRoot: false));
                    }
                }
            }

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
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "PhotoAtomic.Clooney.HashableAttribute")
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
                .Where(p => !HasSkipHash(p));
            
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

    private static bool HasSkipHash(IPropertySymbol property)
    {
        return property.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "PhotoAtomic.Clooney.SkipHashAttribute");
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
            HasCloneMethod = HasHashMethod(type),
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

    private static bool HasHashMethod(ITypeSymbol type)
    {
        return type.GetMembers("HashValue")
            .OfType<IMethodSymbol>()
            .Any(m => m.IsExtensionMethod || m.MethodKind == MethodKind.Ordinary);
    }

    private static string GenerateHashExtensions(
        ClassInfo classInfo,
        Dictionary<INamedTypeSymbol, ClassInfo> reachable,
        List<ClassInfo> allRoots)
    {
        var simpleHash = GenerateSimpleHash(classInfo);
        var trackedHash = GenerateTrackedHash(classInfo, reachable);
        var interfaceImpl = classInfo.IsRoot && classInfo.IsPartial && !classInfo.IsInterface && !classInfo.IsAbstract
            ? GenerateHashableInterface(classInfo, allRoots)
            : null;

        return Indent($$"""
            // <auto-generated/>
            #nullable enable
            using System;
            using System.Linq;
            
            namespace {{classInfo.Namespace}}
            {
                /// <summary>
                /// Extension methods for computing hash values of {{classInfo.ClassName}}
                /// Generated by PhotoAtomic.Clooney
                /// </summary>
                public static partial class {{classInfo.ClassName}}HashExtensions
                {
                    {{simpleHash}}
            
                    {{trackedHash}}
            
                    private static int CombineHash(int hash1, int hash2)
                    {
                        unchecked
                        {
                            // Use a common hash combining algorithm
                            return hash1 * 31 + hash2;
                        }
                    }
            
                    private static T ThrowUnhashable<T>(string message) => throw new global::System.InvalidOperationException(message);
                }
                {{interfaceImpl}}
            }
            """);
    }

    private static string GenerateSimpleHash(ClassInfo classInfo)
    {
        return Indent($$"""
            /// <summary>
            /// Computes a hash value of the {{classInfo.ClassName}} object graph.
            /// Uses an internal HashContext to handle circular references and track reference structure.
            /// </summary>
            public static int HashValue(this {{classInfo.FullyQualifiedName}}? source)
            {
                if (source == null) return 0;
                return source.HashValue(new PhotoAtomic.Clooney.HashContext());
            }
            """);
    }

    private static string GenerateTrackedHash(ClassInfo classInfo, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        var derivedReachable = GetDerivedReachable(classInfo, reachable).ToList();

        if (classInfo.IsInterface || classInfo.IsAbstract)
        {
            return Indent($$"""
                /// <summary>
                /// Computes a hash value with reference tracking (supports circular references).
                /// </summary>
                public static int HashValue(
                    this {{classInfo.FullyQualifiedName}}? source,
                    PhotoAtomic.Clooney.HashContext context)
                {
                    return source switch
                    {
                        null => 0,
                        {{Indent(derivedReachable.Select(derived =>
                            $"{derived.FullyQualifiedName} d => {derived.ClassName}HashExtensions.HashValue(d, context),"))}}
                        _ => source.GetHashCode()
                    };
                }
                """);
        }

        var derivedChecks = derivedReachable.Any()
            ? Indent($$"""
                
                        return source switch
                        {
                            {{Indent(derivedReachable.Select(derived =>
                                $"{derived.FullyQualifiedName} d => {derived.ClassName}HashExtensions.HashValue(d, context),"))}}
                            _ => HashInternal(source, context)
                        };
                    }
                    
                    private static int HashInternal(
                        {{classInfo.FullyQualifiedName}} source,
                        PhotoAtomic.Clooney.HashContext context)
                    {
                """)
            : "";

        return Indent($$"""
            /// <summary>
            /// Computes a hash value with reference tracking (supports circular references).
            /// </summary>
            public static int HashValue(
                this {{classInfo.FullyQualifiedName}}? source,
                PhotoAtomic.Clooney.HashContext context)
            {
                if (source == null) return 0;{{derivedChecks}}
            
                // Get or assign reference ID for this object
                var refId = context.GetOrAssignId(source, out var isNew);
                
                // If already visited (circular reference), return just the reference ID
                if (!isNew)
                {
                    return refId;
                }
            
                // Start with the reference ID to distinguish object instances
                int hash = refId;
                
                // Combine hash with all property values
                {{Indent(classInfo.Properties.Select(prop =>
                {
                    var hashExpr = GenerateHashExpression(classInfo, prop, reachable);
                    return $"hash = CombineHash(hash, {hashExpr});";
                }))}}
                
                
                return hash;
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

    private static string GenerateHashExpression(ClassInfo owner, PropertyInfo prop, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        var sourceAccess = "source." + prop.Name;

        // For value types, use GetHashCode()
        if (prop.IsValueType)
        {
            return $"{sourceAccess}.GetHashCode()";
        }

        // For strings, use GetHashCode() or 0 if null
        if (prop.TypeSymbol.SpecialType == SpecialType.System_String)
        {
            return $"({sourceAccess}?.GetHashCode() ?? 0)";
        }

        // For object/dynamic types with KnownTypes
        if (IsObjectLike(prop.TypeSymbol))
        {
            if (prop.KnownTypes.Count == 0)
            {
                return $"ThrowUnhashable<int>(\"Hashable '{owner.FullyQualifiedName}' property '{prop.Name}' of type '{prop.Type}' must declare [KnownType] to be hashed.\")";
            }

            var cases = new List<string>();
            foreach (var known in prop.KnownTypes)
            {
                var knownDisplay = known.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var hashExpr = GenerateKnownTypeHash("typed", known, reachable);
                cases.Add($"{knownDisplay} typed => {hashExpr}");
            }

            cases.Add($"null => 0");
            cases.Add($"_ => {sourceAccess}.GetHashCode()");
            return $"{sourceAccess} switch {{ {string.Join(", ", cases)} }}";
        }

        // For collections, combine hashes of all elements
        if (prop.IsCollection && prop.CollectionElementTypeSymbol != null)
        {
            var elementReachable = IsReachable(prop.CollectionElementTypeSymbol, reachable);
            var elementHasHash = HasHashMethod(prop.CollectionElementTypeSymbol);
            var elementIsValue = prop.CollectionElementTypeSymbol.IsValueType;

            string elementHashExpr;
            if (elementReachable)
            {
                // For reference types that are reachable (nullable)
                elementHashExpr = "x?.HashValue(context) ?? 0";
            }
            else if (elementHasHash)
            {
                // For reference types with HashValue method (nullable)
                elementHashExpr = "x?.HashValue() ?? 0";
            }
            else if (elementIsValue)
            {
                // For value types, no null check needed
                elementHashExpr = "x.GetHashCode()";
            }
            else if (prop.CollectionElementTypeSymbol.SpecialType == SpecialType.System_String)
            {
                // For strings (reference type but common), use null check
                elementHashExpr = "x?.GetHashCode() ?? 0";
            }
            else
            {
                var elementType = prop.CollectionElementType ?? "object";
                return $"ThrowUnhashable<int>(\"Hashable '{owner.FullyQualifiedName}' property '{prop.Name}' collection element type '{elementType}' cannot be hashed.\")";
            }

            return $"({sourceAccess}?.Aggregate(0, (h, x) => CombineHash(h, {elementHashExpr})) ?? 0)";
        }

        // For reference types
        var typeReachable = IsReachable(prop.TypeSymbol, reachable);

        if (typeReachable)
        {
            return $"({sourceAccess}?.HashValue(context) ?? 0)";
        }

        if (prop.HasCloneMethod) // HasCloneMethod is used here to check for HashValue method
        {
            return $"({sourceAccess}?.HashValue() ?? 0)";
        }

        return $"ThrowUnhashable<int>(\"Hashable '{owner.FullyQualifiedName}' property '{prop.Name}' of type '{prop.Type}' cannot be hashed (no generated or existing HashValue).\")";
    }

    private static string GenerateKnownTypeHash(string identifier, ITypeSymbol typeSymbol, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        if (typeSymbol is INamedTypeSymbol named && IsReachable(named, reachable))
        {
            return $"{identifier}.HashValue(context)";
        }

        if (HasHashMethod(typeSymbol))
        {
            return $"{identifier}.HashValue()";
        }

        if (typeSymbol.IsValueType || typeSymbol.SpecialType == SpecialType.System_String)
        {
            return $"{identifier}.GetHashCode()";
        }

        return $"{identifier}.GetHashCode()";
    }

    private static bool IsReachable(ITypeSymbol type, Dictionary<INamedTypeSymbol, ClassInfo> reachable)
    {
        return type is INamedTypeSymbol named && reachable.ContainsKey(named);
    }


    private static string GenerateHashableInterface(ClassInfo classInfo, List<ClassInfo> allRoots)
    {
        // Find all types that derive from this class (excluding itself and abstract/interface types)
        var derivedTypes = FindDerivedTypes(classInfo, allRoots)
            .Where(d => !SymbolEqualityComparer.Default.Equals(d.Symbol, classInfo.Symbol))
            .Where(d => !d.IsAbstract && !d.IsInterface)
            .ToList();
        
        // Generate switch expression if there are derived types
        string hashBody;
        if (derivedTypes.Count > 0) // Has real derived types
        {
            var switchCases = new System.Text.StringBuilder();
            
            foreach (var derived in derivedTypes)
            {
                if (switchCases.Length > 0)
                    switchCases.AppendLine();
                    
                switchCases.Append($"                {derived.FullyQualifiedName} => {derived.ClassName}HashExtensions.HashValue(({derived.FullyQualifiedName})this),");
            }
            
            hashBody = $$"""
                        return this switch
                        {
                            {{switchCases}}
                            _ => {{classInfo.ClassName}}HashExtensions.HashValue(this)
                        };
            """;
        }
        else
        {
            // Simple case: no derived types
            hashBody = $"            return {classInfo.ClassName}HashExtensions.HashValue(this);";
        }

        return Indent($$"""
            
            public partial class {{classInfo.ClassName}} : PhotoAtomic.Clooney.IHashable
            {
                public int HashValue()
                {
            {{hashBody}}
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
