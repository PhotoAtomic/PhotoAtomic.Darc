using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;

namespace PhotoAtomic.DeepCloner.Generation;

/// <summary>
/// Metadata about a class being cloned
/// </summary>
internal sealed class ClassInfo
{
    public required INamedTypeSymbol Symbol { get; init; }
    public required string Namespace { get; init; }
    public required string ClassName { get; init; }
    public required string FullyQualifiedName { get; init; }
    public required List<PropertyInfo> Properties { get; init; }
    public HashSet<string> ExcludedProperties { get; init; } = new();
    public bool IsInterface { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsPartial { get; init; }
}

/// <summary>
/// Metadata about a property being cloned
/// </summary>
internal sealed class PropertyInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required ITypeSymbol TypeSymbol { get; init; }
    public required bool IsReferenceType { get; init; }
    public required bool IsValueType { get; init; }
    public required bool IsNullable { get; init; }
    public required bool IsCollection { get; init; }
    public required bool HasCloneMethod { get; init; }
    public ITypeSymbol? CollectionElementTypeSymbol { get; init; }
    public string? CollectionElementType { get; init; }
    public IReadOnlyList<ITypeSymbol> KnownTypes { get; init; } = Array.Empty<ITypeSymbol>();
    public bool HasPublicSetter { get; init; }
    public bool NeedsHelperSetter { get; init; }
    public bool IsSettable => HasPublicSetter || NeedsHelperSetter;
}
