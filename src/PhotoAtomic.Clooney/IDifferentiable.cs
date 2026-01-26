using System.Collections.Generic;

namespace PhotoAtomic.Clooney;

/// <summary>
/// Interface for types that support difference computation.
/// Automatically implemented by partial classes marked with [Diffable].
/// </summary>
/// <typeparam name="T">The type being compared</typeparam>
public interface IDifferentiable<in T>
{
    /// <summary>
    /// Computes differences between this instance and another.
    /// </summary>
    /// <param name="other">The instance to compare with</param>
    /// <returns>An enumerable of difference paths</returns>
    IEnumerable<DifferencePath> Diff(T? other);
}
