using System;

namespace PhotoAtomic.DeepCloner;

/// <summary>
/// Marks a property to be excluded from cloning.
/// Properties decorated with this attribute will not be copied during clone operations.
/// </summary>
/// <example>
/// <code>
/// [DeepCopyable]
/// public class MyState
/// {
///     public int Value { get; set; }
///     
///     [SkipClone]
///     public List&lt;Event&gt; PendingEvents { get; set; } // Not cloned
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class SkipCloneAttribute : Attribute
{
}
