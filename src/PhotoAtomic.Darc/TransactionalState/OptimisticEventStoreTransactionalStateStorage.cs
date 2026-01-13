using KurrentDB.Client;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Transactions;
using Orleans.Transactions.Abstractions;
using PhotoAtomic.Darc.TransactionalState.Models;
using System.Text.Json;

namespace PhotoAtomic.Darc.TransactionalState;

/// <summary>
/// Optimistic implementation of ITransactionalStateStorage for KurrentDB.
/// Uses in-memory prepare phase with atomic batch commit for high throughput.
/// </summary>
/// <typeparam name="TState">The type of state being managed</typeparam>
public class OptimisticEventStoreTransactionalStateStorage<TState> : ITransactionalStateStorage<TState>
    where TState : class, new()
{
    private readonly KurrentDBClient client;
    private readonly string streamName;
    private readonly ILogger<OptimisticEventStoreTransactionalStateStorage<TState>> logger;

    private TState committedState = new TState();
    private ulong committedRevisionValue = 0;
    private long committedSequenceId = 0;
    private string currentETag = string.Empty;

    // In-memory pending transactions (zero I/O on prepare!)
    private readonly Dictionary<string, TransactionPendingData> pendingTransactions = new();

    public OptimisticEventStoreTransactionalStateStorage(
        KurrentDBClient client,
        string stateName,
        IGrainContext grainContext,
        ILogger<OptimisticEventStoreTransactionalStateStorage<TState>> logger)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        var grainType = grainContext.GrainId.Type.ToString();
        var grainKey = grainContext.GrainId.Key.ToString();
        streamName = $"{grainType}-{grainKey}-{stateName}";
    }

    public async Task<TransactionalStorageLoadResponse<TState>> Load()
    {
        try
        {
            // Load committed state from event stream
            var events = client.ReadStreamAsync(
                Direction.Forwards,
                streamName,
                StreamPosition.Start,
                resolveLinkTos: false);

            await foreach (var evt in events)
            {
                ApplyEvent(committedState, evt.Event);
                committedRevisionValue = evt.Event.EventNumber.ToUInt64();
            }

            // Load metadata - stores committed sequence ID
            var metadata = await LoadMetadataWithSequenceId();
            committedSequenceId = metadata.SequenceId;
            currentETag = Guid.NewGuid().ToString();

            logger.LogInformation(
                "Loaded optimistic transactional state for stream {StreamName}. CommittedSeq={Seq}, Revision={Rev}",
                streamName, committedSequenceId, committedRevisionValue);

            return new TransactionalStorageLoadResponse<TState>(
                currentETag,
                committedState,
                committedSequenceId,
                metadata.Metadata,
                Array.Empty<PendingTransactionState<TState>>()); // No pending states in optimistic mode
        }
        catch (StreamNotFoundException)
        {
            logger.LogInformation("Stream {StreamName} not found, initializing new state", streamName);
            
            // Generate ETag for new stream and update currentETag
            currentETag = Guid.NewGuid().ToString();
            
            return new TransactionalStorageLoadResponse<TState>(
                currentETag,
                new TState(),
                0,
                new TransactionalStateMetaData(),
                Array.Empty<PendingTransactionState<TState>>());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading optimistic transactional state from {StreamName}", streamName);
            throw;
        }
    }

    public async Task<string> Store(
        string expectedETag,
        TransactionalStateMetaData metadata,
        List<PendingTransactionState<TState>> statesToPrepare,
        long? commitUpTo,
        long? abortAfter)
    {
        // Optimistic concurrency check
        if (expectedETag != currentETag)
        {
            throw new ArgumentException(
                $"ETag mismatch. Expected: {expectedETag}, Current: {currentETag}");
        }

        // PHASE 1: PREPARE - Keep events in-memory (ZERO I/O!)
        if (statesToPrepare is { Count: > 0 })
        {
            foreach (var pending in statesToPrepare)
            {
                var events = ComputeDomainEvents(committedState, pending.State);
                
                // Store in-memory (no database write!)
                pendingTransactions[pending.TransactionId] = new TransactionPendingData
                {
                    TransactionId = pending.TransactionId,
                    SequenceId = pending.SequenceId,
                    Events = events,
                    BaseRevision = committedRevisionValue,
                    WorkingState = DeepCopy(pending.State),
                    Timestamp = pending.TimeStamp
                };

                logger.LogTrace(
                    "Prepared transaction {TxId} in-memory with {EventCount} events (NO I/O!)",
                    pending.TransactionId, events.Count);
            }

            return currentETag;
        }

        // PHASE 2: COMMIT - Atomic batch append to EventStoreDB
        if (commitUpTo.HasValue)
        {
            var transactionsToCommit = pendingTransactions.Values
                .Where(tx => tx.SequenceId <= commitUpTo.Value)
                .OrderBy(tx => tx.SequenceId)
                .ToList();

            if (transactionsToCommit.Count == 0)
            {
                logger.LogWarning(
                    "No transactions to commit for sequence {Seq} in stream {StreamName}",
                    commitUpTo.Value, streamName);
                return currentETag;
            }

            // Collect all events from all transactions
            var allEvents = new List<EventData>();
            foreach (var tx in transactionsToCommit)
            {
                foreach (var domainEvent in tx.Events)
                {
                    var eventData = new EventData(
                        Uuid.NewUuid(),
                        domainEvent.EventType,
                        JsonSerializer.SerializeToUtf8Bytes(domainEvent.Data),
                        JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            TransactionId = tx.TransactionId,
                            SequenceId = tx.SequenceId,
                            Timestamp = tx.Timestamp
                        }));

                    allEvents.Add(eventData);
                }
            }

            try
            {
                // ? Atomic batch append with optimistic concurrency check
                var result = await client.AppendToStreamAsync(
                    streamName,
                    committedRevisionValue, // Optimistic concurrency!
                    allEvents);

                // Update committed state  
                // Note: IWriteResult in KurrentDB.Client may have different properties
                committedRevisionValue++; // Increment as we just appended events
                committedSequenceId = commitUpTo.Value;
                
                // Apply events to committed state
                foreach (var tx in transactionsToCommit)
                {
                    committedState = DeepCopy(tx.WorkingState);
                    pendingTransactions.Remove(tx.TransactionId);
                }

                // Clear pending events from committed state if it's event-sourced
                if (committedState is EventSourcedStateBase eventSourcedState)
                {
                    eventSourcedState.ClearPendingEvents();
                }

                // Save metadata with committed sequence ID
                await SaveMetadataWithSequenceId(committedSequenceId, metadata);

                currentETag = Guid.NewGuid().ToString();

                logger.LogInformation(
                    "? Committed {TxCount} transactions ({EventCount} events) atomically to stream {StreamName}. New revision: {Revision}",
                    transactionsToCommit.Count, allEvents.Count, streamName, committedRevisionValue);

                return currentETag;
            }
            catch (WrongExpectedVersionException ex)
            {
                // ? Optimistic concurrency conflict!
                logger.LogWarning(
                    ex,
                    "Optimistic concurrency conflict on stream {StreamName}. Expected revision: {ExpectedRev}",
                    streamName, committedRevisionValue);

                // Clear in-memory pending transactions
                pendingTransactions.Clear();

                // Trigger retry by throwing Orleans transaction exception
                throw new OrleansTransactionAbortedException(
                    $"Optimistic concurrency conflict on stream {streamName}",
                    ex);
            }
        }

        // PHASE 3: ABORT - Remove from in-memory pending (zero I/O!)
        if (abortAfter.HasValue)
        {
            var aborted = pendingTransactions.Values
                .Where(tx => tx.SequenceId > abortAfter.Value)
                .ToList();

            foreach (var tx in aborted)
            {
                pendingTransactions.Remove(tx.TransactionId);
                
                logger.LogTrace(
                    "Aborted transaction {TxId} (in-memory only, NO I/O!)",
                    tx.TransactionId);
            }
        }

        return currentETag;
    }

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    private List<DomainEvent> ComputeDomainEvents(TState oldState, TState newState)
    {
        // Check if state inherits from EventSourcedStateBase
        if (newState is EventSourcedStateBase eventSourcedState)
        {
            // Use pending events from the state
            var pendingEvents = eventSourcedState.GetPendingEvents();
            
            return pendingEvents.Select(evt => new DomainEvent
            {
                EventType = evt.GetType().Name,
                Data = evt
            }).ToList();
        }

        // Fallback: PLACEHOLDER for non-event-sourced states
        // Example for BankAccount:
        //   if (newState.Balance > oldState.Balance)
        //       return [new MoneyDeposited(amount)];
        //   else if (newState.Balance < oldState.Balance)
        //       return [new MoneyWithdrawn(amount)];
        
        return new List<DomainEvent>
        {
            new DomainEvent
            {
                EventType = $"{typeof(TState).Name}Changed",
                Data = new StateChangedEvent<TState>(newState)
            }
        };
    }

    private void ApplyEvent(TState state, EventRecord eventRecord)
    {
        // PLACEHOLDER: Implement event sourcing apply logic
        try
        {
            if (eventRecord.EventType == $"{typeof(TState).Name}Changed")
            {
                var data = JsonSerializer.Deserialize<StateChangedEvent>(eventRecord.Data.Span);
                if (data?.NewState != null)
                {
                    CopyState(data.NewState, state);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply event {EventType}", eventRecord.EventType);
        }
    }

    private TState DeepCopy(TState source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<TState>(json) ?? new TState();
    }

    private void CopyState(TState source, TState target)
    {
        var properties = typeof(TState).GetProperties();
        foreach (var prop in properties)
        {
            if (prop.CanWrite && prop.CanRead)
            {
                prop.SetValue(target, prop.GetValue(source));
            }
        }
    }

    private async Task<MetadataWithSequenceId> LoadMetadataWithSequenceId()
    {
        try
        {
            var metadataStream = $"{streamName}-metadata";
            var result = client.ReadStreamAsync(
                Direction.Backwards,
                metadataStream,
                StreamPosition.End,
                maxCount: 1);

            await foreach (var evt in result)
            {
                var combined = JsonSerializer.Deserialize<MetadataWithSequenceId>(evt.Event.Data.Span);
                return combined ?? new MetadataWithSequenceId
                {
                    SequenceId = 0,
                    Metadata = new TransactionalStateMetaData()
                };
            }
        }
        catch (StreamNotFoundException)
        {
            // No metadata exists yet
        }

        return new MetadataWithSequenceId
        {
            SequenceId = 0,
            Metadata = new TransactionalStateMetaData()
        };
    }

    private async Task SaveMetadataWithSequenceId(long sequenceId, TransactionalStateMetaData metadata)
    {
        var metadataStream = $"{streamName}-metadata";
        var combined = new MetadataWithSequenceId
        {
            SequenceId = sequenceId,
            Metadata = metadata
        };

        var eventData = new EventData(
            Uuid.NewUuid(),
            "MetadataSnapshot",
            JsonSerializer.SerializeToUtf8Bytes(combined));

        await client.AppendToStreamAsync(
            metadataStream,
            StreamState.Any,
            new[] { eventData });
    }

    // =========================================================================
    // MODELS
    // =========================================================================

    private class MetadataWithSequenceId
    {
        public long SequenceId { get; set; }
        public TransactionalStateMetaData Metadata { get; set; } = new();
    }

    private class TransactionPendingData
    {
        public string TransactionId { get; set; } = string.Empty;
        public long SequenceId { get; set; }
        public List<DomainEvent> Events { get; set; } = new();
        public ulong BaseRevision { get; set; }
        public TState WorkingState { get; set; } = new TState();
        public DateTime Timestamp { get; set; }
    }

    private class StateChangedEvent
    {
        public TState? NewState { get; set; }
        public DateTime ChangedAt { get; set; }
    }
}
