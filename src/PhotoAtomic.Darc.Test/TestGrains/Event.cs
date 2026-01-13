using Orleans;

namespace PhotoAtomic.Darc.Test.TestGrains;

/// <summary>
/// Base record for all domain events in tests
/// </summary>
[GenerateSerializer]
public abstract record Event
{
    /// <summary>
    /// When the event occurred
    /// </summary>
    [Id(0)]
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
