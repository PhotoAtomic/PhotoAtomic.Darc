using Orleans;

namespace PhotoAtomic.Darc.Test.TestGrains;

/// <summary>
/// Base class for event-sourced state in tests
/// </summary>
[GenerateSerializer]
public abstract class EventSourcedStateBase
{
    [Id(0)]
    public List<Event> PendingEventsList { get; set; } = new();

    public IReadOnlyList<Event> GetPendingEvents()
    {
        return PendingEventsList.AsReadOnly();
    }

    public void ClearPendingEvents()
    {
        PendingEventsList.Clear();
    }

    public abstract void Apply(Event evt);

    public void Append(Event evt)
    {
        PendingEventsList.Add(evt);
        Apply(evt);
    }
}
