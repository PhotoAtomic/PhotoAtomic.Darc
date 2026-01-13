using Orleans;
using Orleans.Transactions.Abstractions;

namespace PhotoAtomic.Darc.Test.TestGrains;

/// <summary>
/// Bank account state for testing - Event Sourced
/// State is derived from events, not modified directly
/// </summary>
[GenerateSerializer]
public class BankAccountState : EventSourcedStateBase
{
    // Derived state (computed from events)
    [Id(1)]
    public decimal Balance { get; private set; }
    
    [Id(2)]
    public int TransactionCount { get; private set; }
    
    [Id(3)]
    public DateTime LastUpdate { get; private set; }

    /// <summary>
    /// Apply an event to update the state (overrides EventSourcedStateBase)
    /// Called both for pending events and when rebuilding from event store
    /// </summary>
    public override void Apply(Event evt)
    {
        switch (evt)
        {
            case MoneyDepositedEvent deposited:
                Balance += deposited.Amount;
                TransactionCount++;
                LastUpdate = deposited.OccurredAt;
                break;

            case MoneyWithdrawnEvent withdrawn:
                Balance -= withdrawn.Amount;
                TransactionCount++;
                LastUpdate = withdrawn.OccurredAt;
                break;

            case AccountCreatedEvent created:
                Balance = 0;
                TransactionCount = 0;
                LastUpdate = created.OccurredAt;
                break;

            default:
                throw new InvalidOperationException($"Unknown event type: {evt.GetType().Name}");
        }
    }
}

/// <summary>
/// Test grain implementing bank account with transactional state
/// Uses event sourcing - all changes go through events
/// </summary>
public class BankAccountGrain : EventSourcedGrain<BankAccountState>, IBankAccountGrain
{
    public BankAccountGrain(
        [TransactionalState("account")]
        ITransactionalState<BankAccountState> account)
    {
        InitializeState(account);
    }

    public async Task Deposit(decimal amount)
    {
        if (amount < 0)
            throw new ArgumentException("Deposit amount must be positive or zero", nameof(amount));

        // ? Simplified: Just append the event!
        await Append(new MoneyDepositedEvent(amount));
    }

    public async Task Withdraw(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Withdraw amount must be positive", nameof(amount));

        // ? Use Update when you need to validate business rules
        await Update(state =>
        {
            // Business rule validation BEFORE creating event
            if (state.Balance < amount)
                throw new InvalidOperationException(
                    $"Insufficient funds. Balance: {state.Balance}, Requested: {amount}");

            // Append event after validation
            state.Append(new MoneyWithdrawnEvent(amount));
        });
    }

    public async Task<decimal> GetBalance()
    {
        // ? Simplified: Use Read helper
        return await Read(state => state.Balance);
    }

    public async Task<int> GetTransactionCount()
    {
        // ? Simplified: Use Read helper
        return await Read(state => state.TransactionCount);
    }
}

/// <summary>
/// Coordinator grain for distributed transactions
/// </summary>
public class TransferCoordinatorGrain : Grain, ITransferCoordinatorGrain
{
    public async Task Transfer(string fromAccountId, string toAccountId, decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Transfer amount must be positive", nameof(amount));

        if (fromAccountId == toAccountId)
            throw new ArgumentException("Cannot transfer to same account");

        var fromAccount = GrainFactory.GetGrain<IBankAccountGrain>(fromAccountId);
        var toAccount = GrainFactory.GetGrain<IBankAccountGrain>(toAccountId);

        // Both operations participate in the same distributed transaction
        // Each creates events that will be written to their respective streams
        await fromAccount.Withdraw(amount);
        await toAccount.Deposit(amount);
    }
}
