# PhotoAtomic.Darc

> **Event Sourcing storage provider for [Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/) Transactions, backed by [KurrentDB](https://www.kurrent.io/).**

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Orleans](https://img.shields.io/badge/Orleans-10.x-blue)](https://learn.microsoft.com/en-us/dotnet/orleans/)
[![KurrentDB](https://img.shields.io/badge/KurrentDB-1.4-green)](https://www.kurrent.io/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

---

## What is it?

Orleans has a powerful distributed transactions system, but ships with only an **in-memory** storage backend — not suitable for production. Plugging in a real persistent backend requires implementing `ITransactionalStateStorage<T>`, which involves write-ahead logging, two-phase commit (2PC), concurrency management, and state replay.

**PhotoAtomic.Darc does all of this for you**, using **KurrentDB as the event log**.

The result: your grain state is never modified directly. Instead, you **append immutable events**, KurrentDB durably records them, and state is rebuilt by replaying the stream. Distributed transactions spanning multiple grains are fully atomic — all commit or all roll back.

---

## Why Event Sourcing + Orleans?

| Without Darc | With Darc |
|---|---|
| State is a snapshot, history is lost | Full audit trail — every change is an event |
| In-memory storage only (no persistence) | KurrentDB persistence, survives restarts |
| Manual transaction coordination | Orleans 2PC handles atomicity automatically |
| State mutation is implicit | State mutation is explicit and traceable |

---

## How it works

```
Your Grain
    │
    │  state.Append(new MoneyDepositedEvent(100))
    ▼
ITransactionalState<TState>   ← Orleans Transactions
    │
    │  PREPARE / COMMIT / ABORT
    ▼
EventStoreTransactionalStateStorage   ← Darc
    │
    │  Write events to stream
    ▼
KurrentDB
    └─ stream: "bankaccount-alice-account"
         MoneyDepositedEvent  (+500)
         MoneyWithdrawnEvent  (-200)
         MoneyDepositedEvent  (+100)   ← latest commit
```

Each grain instance maps to its own **KurrentDB stream**. On activation, Darc replays the stream to rebuild state. On commit, new events are appended. On abort, nothing is written.

---

## Key concepts

### `EventSourcedStateBase`

The only coupling point between your state and Darc. Extend it to get:
- `Append(Event evt)` — applies the event to state and queues it for persistence
- `Apply(Event evt)` — override this to define how each event mutates state
- `PendingEventsList` — read by Darc after each transaction to know what to persist

```csharp
[Clonable]           // required: Clooney generates Clone() used by Orleans Transactions
[GenerateSerializer] // required: Orleans serialization
public partial class BankAccountState : EventSourcedStateBase
{
    [Id(1)] public decimal Balance { get; set; }
    [Id(2)] public int TransactionCount { get; set; }

    public override void Apply(Event evt)
    {
        switch (evt)
        {
            case MoneyDepositedEvent e: Balance += e.Amount; TransactionCount++; break;
            case MoneyWithdrawnEvent e: Balance -= e.Amount; TransactionCount++; break;
        }
    }
}
```

### `[Clonable]` and Clooney

Orleans Transactions requires deep-cloning state to support rollback. Darc ships with **Clooney**, a Roslyn source generator that generates a `Clone()` method at compile time — no reflection, no runtime overhead. Just add `[Clonable]` to your state class and reference Clooney as an analyzer.

### `EventSourcedGrain<TState>` — yours to define

Darc deliberately does **not** provide a base grain class. The reason is C# single inheritance: if Darc imposed `EventSourcedGrain<T>`, you could never combine it with your own base grain, an Orleans built-in (`StatelessWorkerGrain`), or any other framework base.

Instead, Darc gives you `EventSourcedStateBase` (the storage contract) and leaves grain structure entirely to you. The recommended pattern — shown in the test project — is to define a thin `EventSourcedGrain<TState>` once in your own codebase:

```csharp
// Define once in your project — copy, adapt, enrich as needed
public abstract class EventSourcedGrain<TState> : Grain
    where TState : EventSourcedStateBase, new()
{
    protected ITransactionalState<TState> State { get; private set; } = null!;

    protected void InitializeState(ITransactionalState<TState> state)
        => State = state ?? throw new ArgumentNullException(nameof(state));

    protected async Task Append(Event evt)
        => await State.PerformUpdate(s => s.Append(evt));

    protected async Task Update(Action<TState> action)
        => await State.PerformUpdate(action);

    protected async Task<TResult> Read<TResult>(Func<TState, TResult> func)
        => await State.PerformRead(func);
}
```

This keeps grain code focused on domain logic and gives you freedom to add cross-cutting concerns (logging, authorization, metrics) without touching Darc.

---

## Quick start

### 1. Register Darc in your silo

```csharp
siloBuilder.UseTransactions();
siloBuilder.ConfigureServices(services =>
    services.AddEventStoreTransactionalStateStorage(
        "esdb://localhost:2113?tls=false"));
```

Use `AddOptimisticEventStoreTransactionalStateStorage` for read-heavy workloads.

### 2. Define events

```csharp
[GenerateSerializer]
public record MoneyDepositedEvent([property: Id(1)] decimal Amount) : Event;

[GenerateSerializer]
public record MoneyWithdrawnEvent([property: Id(1)] decimal Amount) : Event;
```

### 3. Define state

```csharp
[Clonable]
[GenerateSerializer]
public partial class BankAccountState : EventSourcedStateBase
{
    [Id(1)] public decimal Balance { get; set; }

    public override void Apply(Event evt)
    {
        if (evt is MoneyDepositedEvent e) Balance += e.Amount;
        if (evt is MoneyWithdrawnEvent e2) Balance -= e2.Amount;
    }
}
```

### 4. Define grain interface

```csharp
public interface IBankAccountGrain : IGrainWithStringKey
{
    [Transaction(TransactionOption.Create)]
    Task Deposit(decimal amount);

    [Transaction(TransactionOption.Create)]
    Task Withdraw(decimal amount);

    [Transaction(TransactionOption.CreateOrJoin)]
    Task<decimal> GetBalance();
}
```

### 5. Implement grain

```csharp
public class BankAccountGrain : EventSourcedGrain<BankAccountState>, IBankAccountGrain
{
    public BankAccountGrain(
        [TransactionalState("account")] ITransactionalState<BankAccountState> account)
        => InitializeState(account);

    public async Task Deposit(decimal amount) =>
        await Append(new MoneyDepositedEvent(amount));

    public async Task Withdraw(decimal amount) =>
        await Update(state =>
        {
            if (state.Balance < amount)
                throw new InvalidOperationException("Insufficient funds");
            state.Append(new MoneyWithdrawnEvent(amount));
        });

    public async Task<decimal> GetBalance() =>
        await Read(state => state.Balance);
}
```

---

## Distributed transactions

A coordinator grain wraps multiple grain calls in a single `[Transaction(TransactionOption.Create)]`. Orleans handles 2PC automatically — if any participant fails, all are rolled back and no events are written to KurrentDB.

```csharp
public class TransferCoordinatorGrain : Grain, ITransferCoordinatorGrain
{
    [Transaction(TransactionOption.Create)]
    public async Task Transfer(string from, string to, decimal amount)
    {
        var fromGrain = GrainFactory.GetGrain<IBankAccountGrain>(from);
        var toGrain   = GrainFactory.GetGrain<IBankAccountGrain>(to);

        await fromGrain.Withdraw(amount); // participates in outer transaction
        await toGrain.Deposit(amount);    // participates in outer transaction
    }
}
```

---

## Storage strategies

| | Pessimistic (default) | Optimistic |
|---|---|---|
| **Prepare phase** | Writes to a pending stream (WAL) | In-memory only — zero I/O |
| **Commit phase** | Moves events from pending → main stream | Atomic batch append to main stream |
| **Abort phase** | Deletes pending stream | Discards in-memory buffer |
| **Best for** | Write-heavy, high contention | Read-heavy, low contention |
| **Registration** | `AddEventStoreTransactionalStateStorage` | `AddOptimisticEventStoreTransactionalStateStorage` |

In both cases the **main stream contains only committed domain events** — no transaction markers, no metadata. State can always be rebuilt cleanly by replaying the stream.

---

## Testing

The test suite uses **Testcontainers** to spin up a real KurrentDB instance in Docker for integration tests, and **Orleans TestingHost** to run a full in-process silo. No mocks, no fakes — the tests exercise the actual storage layer end to end.

```
PhotoAtomic.Darc.Test/
  TestGrains/                  ← BankAccountGrain, EventSourcedGrain, events and state
                                  used as test subjects (not a standalone example)
  EventStoreTransactionalStateStorageTest.cs  ← integration tests with real KurrentDB
  ContainerizedPersistenceTest.cs             ← low-level KurrentDB container tests
  DeepClonerTests.cs                          ← Clooney Clone() tests
  DiffTests.cs                                ← Clooney Diff() tests
  HashValueTests.cs                           ← Clooney Hash() tests
```

Running the integration tests requires Docker.

---

## Repository structure

```
PhotoAtomic.Darc/
  src/
    PhotoAtomic.Darc/                ← storage provider (this library)
    PhotoAtomic.Clooney/             ← Roslyn source generator (Clone/Diff/Hash)
    PhotoAtomic.Clooney.Abstractions/← attributes: [Clonable], [Diffable], [Hashable]
    PhotoAtomic.IndentedStrings/     ← code generation utility (used by Clooney)
    PhotoAtomic.Darc.Test/           ← integration + unit tests
    Example.*/                       ← Orleans cluster wiring used by the test suite
```

📖 For deeper technical details on the storage implementation, see the **[PhotoAtomic.Darc project README](src/PhotoAtomic.Darc/README.md)**.

---

## Requirements

- .NET 10+
- Microsoft Orleans 10.x
- KurrentDB (community edition is sufficient)
- Docker (for running the integration tests)

---

## License

MIT — see [LICENSE](LICENSE).

---

## References

- [Orleans Transactions documentation](https://learn.microsoft.com/en-us/dotnet/orleans/grains/transactions)
- [KurrentDB documentation](https://developers.kurrent.io/)
- [Event Sourcing pattern](https://martinfowler.com/eaaDev/EventSourcing.html)
