using System;
using System.Collections.Generic;

namespace PhotoAtomic.Clooney;

/// <summary>
/// Tracks object references during hash computation to ensure structural differences
/// are reflected in the hash value, and to prevent infinite loops on circular references.
/// </summary>
/// <remarks>
/// This class ensures that:
/// 1. Each unique object instance gets a unique reference ID
/// 2. Circular references are handled gracefully without infinite loops
/// 3. Two graphs with same values but different structure produce different hashes
/// 
/// Example: 
/// Graph A: Root -> Child1, Root -> Child2 (two different children)
/// Graph B: Root -> Child, Root -> Child (same child referenced twice)
/// These should produce different hashes even if Child1 == Child2 in values.
/// </remarks>
/// <example>
/// <code>
/// var context = new HashContext();
/// var hash = original.HashValue(context);
/// 
/// // If original has circular references, they are tracked by reference ID
/// </code>
/// </example>
public sealed class HashContext
{
    private readonly Dictionary<object, int> _objectIds = new();
    private int _nextId = 1;

    /// <summary>
    /// Gets or assigns a unique reference ID for an object.
    /// Returns the ID if already visited, or assigns a new one.
    /// </summary>
    /// <param name="obj">Object to track</param>
    /// <param name="isNew">True if this is the first time seeing this object</param>
    /// <returns>Unique reference ID for this object</returns>
    public int GetOrAssignId(object obj, out bool isNew)
    {
        if (obj == null)
        {
            isNew = false;
            return 0;
        }

        if (_objectIds.TryGetValue(obj, out var existingId))
        {
            isNew = false;
            return existingId;
        }

        var newId = _nextId++;
        _objectIds[obj] = newId;
        isNew = true;
        return newId;
    }

    /// <summary>
    /// Checks if an object has already been visited.
    /// </summary>
    public bool IsVisited(object obj)
    {
        return obj != null && _objectIds.ContainsKey(obj);
    }

    /// <summary>
    /// Gets the reference ID for an object if it has been visited.
    /// </summary>
    public bool TryGetId(object obj, out int id)
    {
        if (obj == null)
        {
            id = 0;
            return true;
        }

        return _objectIds.TryGetValue(obj, out id);
    }

    /// <summary>
    /// Clear all tracked objects (useful for reusing context).
    /// </summary>
    public void Clear()
    {
        _objectIds.Clear();
        _nextId = 1;
    }
}
