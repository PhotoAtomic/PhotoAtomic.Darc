using System;

namespace PhotoAtomic.Clooney;

/// <summary>
/// Marks a class to have a .Diff() extension method generated.
/// The Diff method compares two object graphs and returns all differences found.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class DiffableAttribute : Attribute
{
    /// <summary>
    /// Comma-separated list of property names to exclude from diff.
    /// </summary>
    public string? Exclude { get; set; }
}
