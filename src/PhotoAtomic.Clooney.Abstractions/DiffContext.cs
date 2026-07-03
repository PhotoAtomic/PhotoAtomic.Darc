using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace PhotoAtomic.Clooney;

/// <summary>
/// Context for tracking visited object pairs during diff operations.
/// Handles circular references and ensures correct structural comparison.
/// </summary>
public sealed class DiffContext
{
    private readonly Dictionary<ObjectPair, VisitInfo> _visited;

    public DiffContext()
    {
        _visited = new Dictionary<ObjectPair, VisitInfo>();
    }

    /// <summary>
    /// Tries to register a pair of objects as visited.
    /// Returns true if this is a new pair (not yet visited).
    /// Returns false if this pair was already visited.
    /// Sets structuralDifference if the source object was visited with a different other object (structural mismatch).
    /// </summary>
    public bool TryRegisterPair(object source, object other, out string? structuralDifferenceMessage)
    {
        structuralDifferenceMessage = null;
        
        if (source == null || other == null)
            return true; // Nulls are handled separately

        var pair = new ObjectPair(source, other);

        if (_visited.TryGetValue(pair, out var info))
        {
            // Already visited this exact pair
            return false;
        }

        // Check if source was visited with a different 'other' object
        foreach (var kvp in _visited)
        {
            if (ReferenceEquals(kvp.Key.Source, source) && !ReferenceEquals(kvp.Key.Other, other))
            {
                // Source object was previously paired with a different object
                // This indicates a structural difference in the graph
                structuralDifferenceMessage = $"Object of type {source.GetType().Name} has different reference structure in the two graphs.";
                return false;
            }

            if (ReferenceEquals(kvp.Key.Other, other) && !ReferenceEquals(kvp.Key.Source, source))
            {
                // Other object was previously paired with a different source object
                // This also indicates a structural difference
                structuralDifferenceMessage = $"Object of type {other.GetType().Name} has different reference structure in the two graphs.";
                return false;
            }
        }

        // Register this new pair
        _visited[pair] = new VisitInfo();
        return true;
    }

    /// <summary>
    /// Checks if an object pair has already been visited.
    /// </summary>
    public bool IsVisited(object source, object other)
    {
        if (source == null || other == null)
            return false;

        return _visited.ContainsKey(new ObjectPair(source, other));
    }

    private struct ObjectPair : IEquatable<ObjectPair>
    {
        public object Source { get; }
        public object Other { get; }

        public ObjectPair(object source, object other)
        {
            Source = source;
            Other = other;
        }

        public bool Equals(ObjectPair other)
        {
            return ReferenceEquals(Source, other.Source) && ReferenceEquals(Other, other.Other);
        }

        public override bool Equals(object? obj)
        {
            return obj is ObjectPair other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (RuntimeHelpers.GetHashCode(Source) * 397) ^ RuntimeHelpers.GetHashCode(Other);
            }
        }
    }

    private class VisitInfo
    {
        // Can be extended in the future to store additional visit information
    }
}

/// <summary>
/// Exception thrown when a structural difference is detected in circular references.
/// </summary>
public sealed class StructuralDifferenceException : Exception
{
    public StructuralDifferenceException(string message) : base(message)
    {
    }
}
