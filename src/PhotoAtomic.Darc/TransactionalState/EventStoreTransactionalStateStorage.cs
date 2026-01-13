using KurrentDB.Client;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using PhotoAtomic.Darc.TransactionalState.Models;
using System.Text.Json;

namespace PhotoAtomic.Darc.TransactionalState;

/// <summary>
/// Pessimistic implementation of ITransactionalStateStorage for KurrentDB.
/// Uses shared pending stream for write-ahead logging.
/// Main stream contains ONLY committed events.
/// Pending stream is shared, fills during PREPARE, empties during COMMIT/ABORT.
/// </summary>
/// <typeparam name="TState">The type of state being managed</typeparam>
public class EventStoreTransactionalStateStorage<TState> : ITransactionalStateStorage<TState>
    where TState : class, new()
{
    private readonly KurrentDBClient client;
    private readonly string streamName;
    private readonly string pendingStreamName;
    private readonly ILogger<EventStoreTransactionalStateStorage<TState>> logger;

    private TState committedState = new TState();
    private ulong committedRevisionValue = 0;
    private long committedSequenceId = 0;
    private string currentETag = string.Empty;

    public EventStoreTransactionalStateStorage(
        KurrentDBClient client,
        string stateName,
        IGrainContext grainContext,
        ILogger<EventStoreTransactionalStateStorage<TState>> logger)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        var grainType = grainContext.GrainId.Type.ToString();
        var grainKey = grainContext.GrainId.Key.ToString();
        streamName = $"{grainType}-{grainKey}-{stateName}";
        pendingStreamName = $"{streamName}-pending"; // Shared pending stream
    }

    public async Task<TransactionalStorageLoadResponse<TState>> Load()
    {
        try
        {
            // Load committed state from MAIN stream (only committed events!)
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

            // Load metadata with sequence ID
            var metadataSnapshot = await LoadMetadataWithSequenceId();
            committedSequenceId = metadataSnapshot.SequenceId;
            currentETag = Guid.NewGuid().ToString();

            logger.LogInformation(
                "Loaded transactional state for stream {StreamName}. CommittedSeq={Seq}, Revision={Rev}",
                streamName, committedSequenceId, committedRevisionValue);

            // Load pending states from shared PENDING stream
            var pendingStates = await LoadPendingStates();

            return new TransactionalStorageLoadResponse<TState>(
                currentETag,
                committedState,
                committedSequenceId,
                metadataSnapshot.Metadata,
                pendingStates);
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
            logger.LogError(ex, "Error loading transactional state from {StreamName}", streamName);
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

        // PHASE 1: PREPARE - Write events to shared PENDING stream
        if (statesToPrepare is { Count: > 0 })
        {
            foreach (var pending in statesToPrepare)
            {
                var events = ComputeDomainEvents(committedState, pending.State);
                
                var eventsToWrite = new List<EventData>();
                foreach (var domainEvent in events)
                {
                    var eventData = new EventData(
                        Uuid.NewUuid(),
                        domainEvent.EventType,
                        JsonSerializer.SerializeToUtf8Bytes(domainEvent.Data),
                        JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            TransactionId = pending.TransactionId,
                            SequenceId = pending.SequenceId,
                            TransactionManager = pending.TransactionManager.ToString(),
                            Timestamp = pending.TimeStamp
                        }));

                    eventsToWrite.Add(eventData);
                }

                // Append to shared PENDING stream
                await client.AppendToStreamAsync(
                    pendingStreamName,
                    StreamState.Any,
                    eventsToWrite);

                logger.LogTrace(
                    "Prepared transaction {TxId} with {EventCount} events in shared pending stream",
                    pending.TransactionId, events.Count);
            }

            // Don't return here - continue to COMMIT phase if commitUpTo is specified
        }

        // PHASE 2: COMMIT - Copy from pending to main stream, then delete pending
        if (commitUpTo.HasValue)
        {
            try
            {
                // Read all events from shared pending stream
                var eventsToCommit = new List<(EventData eventData, long sequenceId)>();
                
                var readPending = client.ReadStreamAsync(
                    Direction.Forwards,
                    pendingStreamName,
                    StreamPosition.Start);

                await foreach (var evt in readPending)
                {
                    var eventMetadata = JsonSerializer.Deserialize<Dictionary<string, object>>(evt.Event.Metadata.Span);
                    if (eventMetadata != null && 
                        eventMetadata.TryGetValue("SequenceId", out var seqObj) &&
                        long.TryParse(seqObj.ToString(), out var seq) &&
                        seq <= commitUpTo.Value)
                    {
                        // Create clean EventData for main stream (WITHOUT transaction metadata)
                        var cleanEventData = new EventData(
                            Uuid.NewUuid(),
                            evt.Event.EventType,
                            evt.Event.Data,
                            ReadOnlyMemory<byte>.Empty); // No metadata in committed events

                        eventsToCommit.Add((cleanEventData, seq));
                    }
                }

                if (eventsToCommit.Count > 0)
                {
                    // Sort by sequence ID to maintain order
                    var orderedEvents = eventsToCommit.OrderBy(e => e.sequenceId).Select(e => e.eventData);

                    // Write to MAIN stream
                    await client.AppendToStreamAsync(
                        streamName,
                        StreamState.Any,
                        orderedEvents);

                    committedSequenceId = commitUpTo.Value;
                    
                    // Update committed state by applying events
                    foreach (var (eventData, _) in eventsToCommit.OrderBy(e => e.sequenceId))
                    {
                        var tempState = DeepCopy(committedState);
                        ApplyEventData(tempState, eventData);
                        committedState = tempState;
                        committedRevisionValue++;
                    }

                    // Clear pending events from committed state if it's event-sourced
                    if (committedState is EventSourcedStateBase eventSourcedState)
                    {
                        eventSourcedState.ClearPendingEvents();
                    }

                    // Save metadata
                    await SaveMetadataWithSequenceId(committedSequenceId, metadata);

                    logger.LogInformation(
                        "Committed {EventCount} events up to sequence {Seq} for stream {StreamName}",
                        eventsToCommit.Count, commitUpTo.Value, streamName);
                }

                // Delete shared pending stream (will be recreated on next PREPARE)
                await DeletePendingStream();
                
                currentETag = Guid.NewGuid().ToString();
                return currentETag;
            }
            catch (StreamNotFoundException)
            {
                // No pending stream exists, nothing to commit
                logger.LogWarning("No pending stream found for commit on {StreamName}", streamName);
                return currentETag;
            }
        }

        // PHASE 3: ABORT - Delete shared pending stream
        if (abortAfter.HasValue)
        {
            await DeletePendingStream();
            
            logger.LogInformation(
                "Aborted transactions after sequence {Seq} for stream {StreamName}",
                abortAfter.Value, streamName);
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
        //   if (newState.Balance != oldState.Balance)
        //       return [new BalanceChanged(newState.Balance - oldState.Balance)];
        
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

    private void ApplyEventData(TState state, EventData eventData)
    {
        // Apply EventData (used during commit from pending)
        try
        {
            var eventType = eventData.Type;
            if (eventType == $"{typeof(TState).Name}Changed")
            {
                var data = JsonSerializer.Deserialize<StateChangedEvent>(eventData.Data.Span);
                if (data?.NewState != null)
                {
                    CopyState(data.NewState, state);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply event data {EventType}", eventData.Type);
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

    private async Task<IReadOnlyList<PendingTransactionState<TState>>> LoadPendingStates()
    {
        var pendingStates = new Dictionary<string, PendingTransactionState<TState>>();

        try
        {
            // Read from shared pending stream
            var events = client.ReadStreamAsync(
                Direction.Forwards,
                pendingStreamName,
                StreamPosition.Start);

            await foreach (var evt in events)
            {
                var eventMetadata = JsonSerializer.Deserialize<Dictionary<string, object>>(evt.Event.Metadata.Span);
                if (eventMetadata != null && 
                    eventMetadata.TryGetValue("TransactionId", out var txIdObj) &&
                    eventMetadata.TryGetValue("SequenceId", out var seqObj))
                {
                    var txId = txIdObj.ToString() ?? string.Empty;
                    var seq = long.Parse(seqObj.ToString() ?? "0");

                    if (seq > committedSequenceId)
                    {
                        // Get or create pending state for this transaction
                        if (!pendingStates.TryGetValue(txId, out var pendingState))
                        {
                            var timestamp = eventMetadata.ContainsKey("Timestamp") 
                                ? DateTime.Parse(eventMetadata["Timestamp"].ToString() ?? DateTime.UtcNow.ToString())
                                : DateTime.UtcNow;

                            pendingState = new PendingTransactionState<TState>
                            {
                                TransactionId = txId,
                                SequenceId = seq,
                                State = DeepCopy(committedState),
                                TimeStamp = timestamp
                            };
                            pendingStates[txId] = pendingState;
                        }

                        // Apply event to pending state
                        ApplyEvent(pendingState.State, evt.Event);
                    }
                }
            }
        }
        catch (StreamNotFoundException)
        {
            // No pending stream exists
            logger.LogTrace("No pending stream found for {StreamName}", streamName);
        }

        return pendingStates.Values.ToList();
    }

    private async Task DeletePendingStream()
    {
        try
        {
            // Use DeleteAsync to allow stream name reuse
            await client.DeleteAsync(pendingStreamName, StreamState.Any);
            logger.LogTrace("Deleted shared pending stream {PendingStreamName}", pendingStreamName);
        }
        catch (StreamNotFoundException)
        {
            // Stream already doesn't exist
            logger.LogTrace("Pending stream {PendingStreamName} not found (already deleted)", pendingStreamName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete pending stream {PendingStreamName}", pendingStreamName);
        }
    }

    // =========================================================================
    // MODELS
    // =========================================================================

    private class MetadataWithSequenceId
    {
        public long SequenceId { get; set; }
        public TransactionalStateMetaData Metadata { get; set; } = new();
    }

    private class StateChangedEvent
    {
        public TState? NewState { get; set; }
        public DateTime ChangedAt { get; set; }
    }
}
