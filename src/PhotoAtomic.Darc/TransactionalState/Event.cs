using Orleans;

namespace PhotoAtomic.Darc.TransactionalState;

/// <summary>
/// Base record for all domain events
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

/// <summary>
/// Generic state changed event for states that don't implement event sourcing
/// Used as fallback when state doesn't use proper event sourcing
/// </summary>
[GenerateSerializer]
public record StateChangedEvent<TState>(
    [property: Id(1)] TState NewState) : Event;
