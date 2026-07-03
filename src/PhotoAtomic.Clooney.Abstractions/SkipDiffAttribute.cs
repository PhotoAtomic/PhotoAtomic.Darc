using System;

namespace PhotoAtomic.Clooney;

/// <summary>
/// Marks a property to be skipped during diff generation.
/// Properties with this attribute will not be compared.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SkipDiffAttribute : Attribute
{
}
