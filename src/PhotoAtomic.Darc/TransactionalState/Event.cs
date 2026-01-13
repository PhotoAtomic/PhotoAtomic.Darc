namespace PhotoAtomic.Darc.TransactionalState;

/// <summary>
/// Base record for all domain events
/// Do NOT add [GenerateSerializer] here - concrete events in grain projects will have it
/// </summary>
public abstract record Event
{
    /// <summary>
    /// When the event occurred
    /// </summary>
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Generic state changed event for states that don't implement event sourcing
/// Used as fallback when state doesn't use proper event sourcing
/// </summary>
public record StateChangedEvent<TState>(TState NewState) : Event;
