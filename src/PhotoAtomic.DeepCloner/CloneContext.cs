using System;
using System.Collections.Generic;

namespace PhotoAtomic.Clooney;

/// <summary>
/// Tracks cloned objects during deep cloning to preserve reference identity
/// and prevent infinite loops on circular references.
/// </summary>
/// <remarks>
/// This class uses a two-phase cloning approach:
/// 1. Create empty shell
/// 2. Register in context (before populating)
/// 3. Populate properties (can now reference shell for cycles)
/// 
/// This ensures that Clone(A -> B -> A) = A' -> B' -> A' (same A' instance).
/// </remarks>
/// <example>
/// <code>
/// var context = new CloneContext();
/// var cloned = original.Clone(context);
/// 
/// // If original had circular references, they are preserved in cloned
/// </code>
/// </example>
public sealed class CloneContext
{
    private readonly Dictionary<object, object> _clonedObjects = new();
    
    /// <summary>
    /// Gets or creates a clone using two-phase cloning (supports circular references).
    /// Phase 1: Create shell
    /// Phase 2: Register immediately (before populating)
    /// Phase 3: Populate (can now reference shell via context)
    /// </summary>
    /// <typeparam name="T">Type being cloned</typeparam>
    /// <param name="source">Source object to clone</param>
    /// <param name="shellFactory">Factory to create empty shell</param>
    /// <param name="populateAction">Action to populate the shell</param>
    /// <returns>Cloned object with preserved reference structure</returns>
    public T GetOrClone<T>(T source, Func<T> shellFactory, Action<T, CloneContext> populateAction) 
        where T : class
    {
        if (source == null) return null!;
        
        // Check if already cloned (handles cycles)
        if (_clonedObjects.TryGetValue(source, out var existing))
        {
            return (T)existing;
        }
        
        // PHASE 1: Create empty shell
        var clone = shellFactory();
        
        // PHASE 2: Register IMMEDIATELY (critical for circular references)
        _clonedObjects[source] = clone;
        
        // PHASE 3: Populate (can now reference 'clone' via context)
        populateAction(clone, this);
        
        return clone;
    }
    
    /// <summary>
    /// Simple clone without two-phase (for types without potential cycles).
    /// Slightly faster but doesn't support circular references.
    /// </summary>
    /// <typeparam name="T">Type being cloned</typeparam>
    /// <param name="source">Source object to clone</param>
    /// <param name="cloneFactory">Factory that creates the clone</param>
    /// <returns>Cloned object</returns>
    public T GetOrClone<T>(T source, Func<CloneContext, T> cloneFactory) 
        where T : class
    {
        if (source == null) return null!;
        
        if (_clonedObjects.TryGetValue(source, out var existing))
        {
            return (T)existing;
        }
        
        var clone = cloneFactory(this);
        _clonedObjects[source] = clone;
        return clone;
    }
    
    /// <summary>
    /// Manually register a cloned object (for advanced scenarios).
    /// </summary>
    public void Register<T>(T source, T clone) where T : class
    {
        if (source != null && clone != null)
        {
            _clonedObjects[source] = clone;
        }
    }
    
    /// <summary>
    /// Clear all tracked clones (useful for reusing context).
    /// </summary>
    public void Clear()
    {
        _clonedObjects.Clear();
    }
    
    /// <summary>
    /// Number of objects currently tracked.
    /// </summary>
    public int Count => _clonedObjects.Count;
}
