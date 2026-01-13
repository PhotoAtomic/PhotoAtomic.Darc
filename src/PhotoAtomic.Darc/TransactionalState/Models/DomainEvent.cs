namespace PhotoAtomic.Darc.TransactionalState.Models;

/// <summary>
/// Represents a domain event in the event sourcing model.
/// Wrapper for Event with metadata for persistence.
/// </summary>
public class DomainEvent
{
    /// <summary>
    /// The type of event (e.g., "MoneyDepositedEvent", "AccountCreatedEvent").
    /// Typically the name of the Event-derived class.
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// The actual event data (must derive from Event record).
    /// </summary>
    public Event Data { get; set; } = null!;

    /// <summary>
    /// Timestamp when the event occurred (from Event.OccurredAt).
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
