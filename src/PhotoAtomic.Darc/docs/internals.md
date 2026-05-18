# PhotoAtomic.Darc — Internal Technical Details

> 📖 Looking for usage and quick start? See the [PhotoAtomic.Darc README](../README.md) or the [repository root README](../../../README.md).

---

## Stream naming convention

Each grain instance gets dedicated KurrentDB streams, named after grain type, key and state name:

```
{grainType}-{grainKey}-{stateName}           ← committed events only (rebuilt on activation)
{grainType}-{grainKey}-{stateName}-pending   ← write-ahead log (pessimistic only, ephemeral)
{grainType}-{grainKey}-{stateName}-metadata  ← transaction sequence id metadata
```

The **main stream is always clean**: it contains only committed domain events, with no transaction markers or metadata. You can inspect or replay it from any KurrentDB client independently of Darc.

---

## Pessimistic storage (`EventStoreTransactionalStateStorage`)

Implements `ITransactionalStateStorage<T>` with a **write-ahead log** on a shared pending stream.

```
PREPARE
  └─ Append events to {stream}-pending with transaction metadata (txId, seqId)
     Multiple concurrent transactions share the same pending stream.

COMMIT
  └─ Read events from {stream}-pending filtered by seqId <= commitUpTo
  └─ Append clean events (no metadata) to main stream
  └─ Delete {stream}-pending  ← freed for the next prepare cycle

ABORT
  └─ Delete {stream}-pending
```

**Properties:**
- Durable prepare phase — survives silo crashes between prepare and commit
- Supports multiple concurrent transactions on the same state (shared pending stream)
- Two write operations per transaction (pending + main)
- Works with any KurrentDB version

---

## Optimistic storage (`OptimisticEventStoreTransactionalStateStorage`)

Implements `ITransactionalStateStorage<T>` with an **in-memory prepare phase**.

```
PREPARE
  └─ Events stored in-memory only — zero I/O

COMMIT
  └─ Atomic multi-stream batch append to main stream (KurrentDB MultiStreamAppendAsync)
  └─ Optimistic concurrency check — retries automatically on conflict

ABORT
  └─ Discard in-memory buffer — zero I/O
```

**Properties:**
- Zero I/O during prepare — significantly lower latency for 2PC
- Atomic batch commit via `MultiStreamAppendAsync`
- Higher memory footprint during prepare phase
- Under high write contention, retry overhead may offset the latency gains

---

## `EventSourcedStateBase`

The contract between your application state and the storage layer. Darc reads `PendingEventsList` after each `PerformUpdate` to determine what events to persist.

| Member | Purpose |
|---|---|
| `Append(Event evt)` | Applies the event via `Apply()` and adds it to `PendingEventsList` |
| `Apply(Event evt)` | **Override this** — defines how each event mutates state |
| `PendingEventsList` | Collected by Darc after each transaction; marked `[SkipClone]` so clones always start empty |
| `ClearPendingEvents()` | Called by Darc after events are persisted |

---

## DI registration

```csharp
// Pessimistic (default)
services.AddEventStoreTransactionalStateStorage("esdb://localhost:2113?tls=false");

// Optimistic
services.AddOptimisticEventStoreTransactionalStateStorage("esdb://localhost:2113?tls=false");

// If KurrentDBClient is already registered separately
services.AddEventStoreTransactionalStateStorage(useOptimistic: false);
```

All overloads register `ITransactionalStateStorageFactory` — picked up automatically by Orleans for any grain using `[TransactionalState]`.
