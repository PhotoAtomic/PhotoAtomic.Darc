namespace PhotoAtomic.Clooney;

/// <summary>
/// Interface for types that support deep cloning.
/// Automatically implemented by partial classes marked with [Clonable].
/// </summary>
/// <typeparam name="T">The type being cloned</typeparam>
public interface IClonable<out T>
{
    /// <summary>
    /// Creates a deep clone of this instance.
    /// </summary>
    /// <returns>A deep clone of this instance</returns>
    T? Clone();
}
