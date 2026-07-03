using System;

namespace PhotoAtomic.Clooney;

/// <summary>
/// Marks a class for automatic hash value computation extension method generation.
/// The source generator will create HashValue() extension methods that compute
/// a hash of the entire object graph, including reference structure and all value properties.
/// </summary>
/// <remarks>
/// The hash calculation:
/// - Includes all value-type properties
/// - Tracks object references numerically to distinguish identical values with different structure
/// - Handles circular references gracefully
/// - Produces different hashes for different object graphs even if values are the same
/// </remarks>
/// <example>
/// <code>
/// [Hashable]
/// public class MyState
/// {
///     public int Value { get; set; }
///     public MyState? Next { get; set; }
/// }
/// 
/// // Generated extension methods:
/// var hash = myState.HashValue();
/// 
/// // With reference tracking for circular references:
/// var context = new HashContext();
/// var hash = myState.HashValue(context);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class HashableAttribute : Attribute
{
    /// <summary>
    /// Comma-separated list of property names to exclude from hash calculation.
    /// </summary>
    /// <example>
    /// [Hashable(Exclude = "PendingEventsList,CachedData")]
    /// </example>
    public string? Exclude { get; set; }
}
