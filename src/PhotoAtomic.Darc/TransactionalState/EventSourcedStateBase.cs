using Orleans;

namespace PhotoAtomic.Darc.TransactionalState;

/// <summary>
/// Base class for event-sourced state that tracks pending events
/// Provides default implementations for event sourcing pattern
/// </summary>
[GenerateSerializer]
public abstract class EventSourcedStateBase
{
    /// <summary>
    /// Pending events (not yet committed to event store)
    /// </summary>
    [Id(0)]
    public List<Event> PendingEventsList { get; set; } = new();

    /// <summary>
    /// Get pending events that need to be persisted (read-only view)
    /// </summary>
    public IReadOnlyList<Event> GetPendingEvents()
    {
        return PendingEventsList.AsReadOnly();
    }

    /// <summary>
    /// Clear pending events after they've been committed
    /// </summary>
    public void ClearPendingEvents()
    {
        PendingEventsList.Clear();
    }

    /// <summary>
    /// Apply an event to update the state
    /// Must be overridden by concrete state classes to handle specific event types
    /// </summary>
    public abstract void Apply(Event evt);

    /// <summary>
    /// Append an event to pending list and apply it to state
    /// This is the main method to modify state in event sourcing
    /// </summary>
    public void Append(Event evt)
    {
        // Add to pending events list
        PendingEventsList.Add(evt);
        
        // Apply the event to update state
        Apply(evt);
    }
}
