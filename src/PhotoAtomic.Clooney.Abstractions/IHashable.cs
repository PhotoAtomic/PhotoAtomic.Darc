namespace PhotoAtomic.Clooney;

/// <summary>
/// Interface for types that support hash value computation.
/// Automatically implemented by partial classes marked with [Hashable].
/// </summary>
public interface IHashable
{
    /// <summary>
    /// Computes a hash value of this instance.
    /// </summary>
    /// <returns>A hash value representing this instance</returns>
    int HashValue();
}
