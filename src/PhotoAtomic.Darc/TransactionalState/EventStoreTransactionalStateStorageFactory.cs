using KurrentDB.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace PhotoAtomic.Darc.TransactionalState;

/// <summary>
/// Factory for creating EventStoreTransactionalStateStorage instances
/// Implements Orleans ITransactionalStateStorageFactory interface
/// </summary>
public class EventStoreTransactionalStateStorageFactory : ITransactionalStateStorageFactory
{
    private readonly IServiceProvider serviceProvider;
    private readonly bool useOptimistic;

    public EventStoreTransactionalStateStorageFactory(
        IServiceProvider serviceProvider,
        bool useOptimistic = false)
    {
        this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        this.useOptimistic = useOptimistic;
    }

    /// <summary>
    /// Create a transactional state storage instance for the given state name
    /// Orleans passes the grain context here, not from DI
    /// </summary>
    public ITransactionalStateStorage<TState> Create<TState>(string stateName, IGrainContext context)
        where TState : class, new()
    {
        // Get required services from DI
        var client = serviceProvider.GetRequiredService<KurrentDBClient>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        // Use the context passed by Orleans, NOT from service provider
        if (useOptimistic)
        {
            // Create optimistic storage (in-memory prepare, atomic commit)
            var logger = loggerFactory.CreateLogger<OptimisticEventStoreTransactionalStateStorage<TState>>();
            return new OptimisticEventStoreTransactionalStateStorage<TState>(
                client,
                stateName,
                context, // ? Orleans passes this!
                logger);
        }
        else
        {
            // Create pessimistic storage (write-ahead log with pending stream)
            var logger = loggerFactory.CreateLogger<EventStoreTransactionalStateStorage<TState>>();
            return new EventStoreTransactionalStateStorage<TState>(
                client,
                stateName,
                context, // ? Orleans passes this!
                logger);
        }
    }
}
