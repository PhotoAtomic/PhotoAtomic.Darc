using System;

namespace PhotoAtomic.Clooney;

/// <summary>
/// Marks a property to be excluded from hash value computation.
/// </summary>
/// <example>
/// <code>
/// [Hashable]
/// public class MyState
/// {
///     public int Value { get; set; }
///     
///     [SkipHash]
///     public DateTime LastModified { get; set; }  // Not included in hash
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class SkipHashAttribute : Attribute
{
}
