using KurrentDB.Client;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Transactions.Abstractions;
using PhotoAtomic.Darc.TransactionalState;

namespace PhotoAtomic.Darc.Extensions;

/// <summary>
/// Extension methods for configuring PhotoAtomic.Darc transactional state storage.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds KurrentDB-based transactional state storage using the pessimistic approach.
    /// Uses write-ahead logging with pending events stored in a separate stream.
    /// Best for write-heavy workloads and high contention scenarios.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="kurrentDbConnectionString">KurrentDB connection string</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddEventStoreTransactionalStateStorage(
        this IServiceCollection services,
        string kurrentDbConnectionString)
    {
        // Register KurrentDB client if not already registered
        services.AddSingleton(sp =>
        {
            var settings = KurrentDBClientSettings.Create(kurrentDbConnectionString);
            return new KurrentDBClient(settings);
        });

        // Register the transactional state storage factory
        services.AddSingleton<ITransactionalStateStorageFactory>(sp =>
            new EventStoreTransactionalStateStorageFactory(sp, useOptimistic: false));

        return services;
    }

    /// <summary>
    /// Adds KurrentDB-based transactional state storage using the optimistic approach.
    /// Uses in-memory prepare phase with atomic batch commit for high throughput.
    /// Best for read-heavy workloads with low contention.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="kurrentDbConnectionString">KurrentDB connection string</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddOptimisticEventStoreTransactionalStateStorage(
        this IServiceCollection services,
        string kurrentDbConnectionString)
    {
        // Register KurrentDB client if not already registered
        services.AddSingleton(sp =>
        {
            var settings = KurrentDBClientSettings.Create(kurrentDbConnectionString);
            return new KurrentDBClient(settings);
        });

        // Register the transactional state storage factory
        services.AddSingleton<ITransactionalStateStorageFactory>(sp =>
            new EventStoreTransactionalStateStorageFactory(sp, useOptimistic: true));

        return services;
    }

    /// <summary>
    /// Adds KurrentDB-based transactional state storage using an existing KurrentDBClient.
    /// Assumes KurrentDBClient is already registered in the service collection.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="useOptimistic">True to use optimistic approach, false for pessimistic</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddEventStoreTransactionalStateStorage(
        this IServiceCollection services,
        bool useOptimistic = false)
    {
        // Register the transactional state storage factory
        // Assumes KurrentDBClient is already registered
        services.AddSingleton<ITransactionalStateStorageFactory>(sp =>
            new EventStoreTransactionalStateStorageFactory(sp, useOptimistic));

        return services;
    }

    /// <summary>
    /// Legacy method for backward compatibility - registers for specific state type
    /// Use the factory-based methods instead for better Orleans integration
    /// </summary>
    [Obsolete("Use AddEventStoreTransactionalStateStorage without type parameter instead")]
    public static IServiceCollection AddEventStoreTransactionalState<TState>(
        this IServiceCollection services,
        string stateName,
        bool useOptimistic = false)
        where TState : class, new()
    {
        return AddEventStoreTransactionalStateStorage(services, useOptimistic);
    }
}
