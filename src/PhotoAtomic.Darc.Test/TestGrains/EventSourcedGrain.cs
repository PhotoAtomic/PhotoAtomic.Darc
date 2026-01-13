using Orleans;
using Orleans.Transactions.Abstractions;
using PhotoAtomic.Darc.TransactionalState;

namespace PhotoAtomic.Darc.Test.TestGrains;

/// <summary>
/// Base grain class for event-sourced grains
/// Provides simplified event append mechanism
/// </summary>
/// <typeparam name="TState">The state type that inherits from EventSourcedStateBase</typeparam>
public abstract class EventSourcedGrain<TState> : Grain
    where TState : EventSourcedStateBase, new()
{
    /// <summary>
    /// The transactional state managed by this grain
    /// </summary>
    protected ITransactionalState<TState> State { get; private set; } = null!;

    /// <summary>
    /// Initialize the grain with transactional state
    /// Must be called from derived constructor
    /// </summary>
    protected void InitializeState(ITransactionalState<TState> state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>
    /// Append an event to the transactional state
    /// Simplified helper method that wraps PerformUpdate
    /// </summary>
    /// <param name="evt">The event to append</param>
    protected async Task Append(Event evt)
    {
        await State.PerformUpdate(state =>
        {
            state.Append(evt);
        });
    }

    /// <summary>
    /// Append multiple events to the transactional state
    /// All events are added in a single transaction
    /// </summary>
    /// <param name="events">The events to append</param>
    protected async Task AppendMany(params Event[] events)
    {
        await State.PerformUpdate(state =>
        {
            foreach (var evt in events)
            {
                state.Append(evt);
            }
        });
    }

    /// <summary>
    /// Perform a custom update operation with access to state
    /// Use when you need to validate business rules before appending events
    /// </summary>
    /// <param name="action">The action to perform on the state</param>
    protected async Task Update(Action<TState> action)
    {
        await State.PerformUpdate(action);
    }

    /// <summary>
    /// Read from the state
    /// </summary>
    /// <typeparam name="TResult">The result type</typeparam>
    /// <param name="func">Function to read from state</param>
    /// <returns>The result of the read operation</returns>
    protected async Task<TResult> Read<TResult>(Func<TState, TResult> func)
    {
        return await State.PerformRead(func);
    }
}
