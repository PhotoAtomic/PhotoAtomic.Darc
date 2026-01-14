using System;

namespace PhotoAtomic.DeepCloner;

/// <summary>
/// Marks a class for automatic deep copy extension method generation.
/// The source generator will create Clone() extension methods that support
/// circular references and preserve object graph structure.
/// </summary>
/// <example>
/// <code>
/// [DeepCopyable]
/// public class MyState
/// {
///     public int Value { get; set; }
///     public MyState? Next { get; set; }
/// }
/// 
/// // Generated extension methods:
/// var clone = myState.Clone();
/// 
/// // With reference tracking for circular references:
/// var context = new CloneContext();
/// var clone = myState.Clone(context);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class DeepCopyableAttribute : Attribute
{
    /// <summary>
    /// Comma-separated list of property names to exclude from cloning.
    /// </summary>
    /// <example>
    /// [DeepCopyable(Exclude = "PendingEventsList,CachedData")]
    /// </example>
    public string? Exclude { get; set; }
    
}
