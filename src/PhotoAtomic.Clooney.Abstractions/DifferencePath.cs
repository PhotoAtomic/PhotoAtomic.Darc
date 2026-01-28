using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PhotoAtomic.Clooney;

/// <summary>
/// Represents a path to a difference found between two object graphs.
/// The path consists of a chain of nodes leading from the root to the location of the difference.
/// </summary>
public sealed class DifferencePath
{
    /// <summary>
    /// The root node of the difference path (closest to the root object).
    /// </summary>
    public DifferencePathNode Root { get; }

    /// <summary>
    /// The leaf node of the difference path (the actual difference location).
    /// </summary>
    public DifferencePathNode Leaf { get; }

    public DifferencePath(DifferencePathNode root, DifferencePathNode leaf)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        Leaf = leaf ?? throw new ArgumentNullException(nameof(leaf));
    }

    /// <summary>
    /// Creates a simple path with a single node (for root-level differences).
    /// </summary>
    public DifferencePath(DifferencePathNode singleNode)
        : this(singleNode, singleNode)
    {
    }

    /// <summary>
    /// Gets all nodes in the path from root to leaf.
    /// </summary>
    public IEnumerable<DifferencePathNode> GetPath()
    {
        var current = Root;
        while (current != null)
        {
            yield return current;
            if (current == Leaf)
                break;
            current = current.Next;
        }
    }

    /// <summary>
    /// Returns a human-readable string representation of the difference path.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        var nodes = GetPath().ToList();
        
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (i > 0)
                sb.Append(" -> ");
            
            sb.Append(node.ToString());
        }

        return sb.ToString();
    }
}

/// <summary>
/// Base class for nodes in a difference path.
/// </summary>
public abstract class DifferencePathNode
{
    /// <summary>
    /// The next node in the path (closer to the leaf), or null if this is the leaf.
    /// </summary>
    public DifferencePathNode? Next { get; set; }

    /// <summary>
    /// Appends a node to the end of this path.
    /// </summary>
    public DifferencePathNode Append(DifferencePathNode next)
    {
        if (Next == null)
        {
            Next = next;
            return next;
        }
        return Next.Append(next);
    }

    /// <summary>
    /// Gets the leaf node (last node in the chain).
    /// </summary>
    public DifferencePathNode GetLeaf()
    {
        var current = this;
        while (current.Next != null)
            current = current.Next;
        return current;
    }
}

/// <summary>
/// Represents a reference node in the path - a property that contains a reference type.
/// </summary>
public sealed class ReferenceNode : DifferencePathNode
{
    /// <summary>
    /// The name of the property.
    /// </summary>
    public string PropertyName { get; }

    /// <summary>
    /// The fully qualified type name of the referenced object.
    /// </summary>
    public string TypeName { get; }

    /// <summary>
    /// The current object reference (from the source object graph).
    /// </summary>
    public object? CurrentReference { get; }

    /// <summary>
    /// The other object reference (from the other object graph being compared).
    /// </summary>
    public object? OtherReference { get; }

    public ReferenceNode(
        string propertyName,
        string typeName,
        object? currentReference,
        object? otherReference)
    {
        PropertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
        TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
        CurrentReference = currentReference;
        OtherReference = otherReference;
    }

    public override string ToString()
    {
        return $"{PropertyName}:{TypeName}";
    }
}

/// <summary>
/// Represents a value node in the path - the actual location where values differ.
/// </summary>
public sealed class ValueNode : DifferencePathNode
{
    /// <summary>
    /// The name of the property where the difference was found.
    /// </summary>
    public string PropertyName { get; }

    /// <summary>
    /// The type name of the property.
    /// </summary>
    public string TypeName { get; }

    /// <summary>
    /// The value in the current/source object graph.
    /// </summary>
    public object? CurrentValue { get; }

    /// <summary>
    /// The value in the other object graph being compared.
    /// </summary>
    public object? OtherValue { get; }

    public ValueNode(
        string propertyName,
        string typeName,
        object? currentValue,
        object? otherValue)
    {
        PropertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
        TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
        CurrentValue = currentValue;
        OtherValue = otherValue;
    }

    public override string ToString()
    {
        return $"{PropertyName}:{TypeName} [{CurrentValue} != {OtherValue}]";
    }
}

/// <summary>
/// Represents a collection element node in the path.
/// </summary>
public sealed class CollectionElementNode : DifferencePathNode
{
    /// <summary>
    /// The name of the collection property.
    /// </summary>
    public string CollectionName { get; }

    /// <summary>
    /// The index or key of the element in the collection.
    /// </summary>
    public object IndexOrKey { get; }

    /// <summary>
    /// The type name of the collection element.
    /// </summary>
    public string ElementTypeName { get; }

    public CollectionElementNode(
        string collectionName,
        object indexOrKey,
        string elementTypeName)
    {
        CollectionName = collectionName ?? throw new ArgumentNullException(nameof(collectionName));
        IndexOrKey = indexOrKey ?? throw new ArgumentNullException(nameof(indexOrKey));
        ElementTypeName = elementTypeName ?? throw new ArgumentNullException(nameof(elementTypeName));
    }

    public override string ToString()
    {
        return $"{CollectionName}[{IndexOrKey}]:{ElementTypeName}";
    }
}

/// <summary>
/// Represents a difference in collection size.
/// </summary>
public sealed class CollectionSizeNode : DifferencePathNode
{
    /// <summary>
    /// The name of the collection property.
    /// </summary>
    public string CollectionName { get; }

    /// <summary>
    /// The size of the current collection.
    /// </summary>
    public int CurrentSize { get; }

    /// <summary>
    /// The size of the other collection.
    /// </summary>
    public int OtherSize { get; }

    public CollectionSizeNode(
        string collectionName,
        int currentSize,
        int otherSize)
    {
        CollectionName = collectionName ?? throw new ArgumentNullException(nameof(collectionName));
        CurrentSize = currentSize;
        OtherSize = otherSize;
    }

    public override string ToString()
    {
        return $"{CollectionName}.Count [{CurrentSize} != {OtherSize}]";
    }
}

/// <summary>
/// Represents a difference in circular reference structure.
/// </summary>
public sealed class CircularReferenceNode : DifferencePathNode
{
    /// <summary>
    /// The name of the property where the circular reference differs.
    /// </summary>
    public string PropertyName { get; }

    /// <summary>
    /// Description of the difference in circular structure.
    /// </summary>
    public string Description { get; }

    public CircularReferenceNode(string propertyName, string description)
    {
        PropertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    public override string ToString()
    {
        return $"{PropertyName} [Circular: {Description}]";
    }
}
