using Orleans;

namespace PhotoAtomic.Darc.Test.TestGrains;

/// <summary>
/// Test grain interface for bank account with transactional operations
/// </summary>
public interface IBankAccountGrain : IGrainWithStringKey
{
    /// <summary>
    /// Deposit money into account (transactional)
    /// </summary>
    [Transaction(TransactionOption.Create)]
    Task Deposit(decimal amount);

    /// <summary>
    /// Withdraw money from account (transactional)
    /// </summary>
    [Transaction(TransactionOption.Create)]
    Task Withdraw(decimal amount);

    /// <summary>
    /// Get current balance (read-only, joins existing transaction or creates new)
    /// </summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task<decimal> GetBalance();

    /// <summary>
    /// Get transaction history count (read-only, joins existing transaction or creates new)
    /// </summary>
    [Transaction(TransactionOption.CreateOrJoin)]
    Task<int> GetTransactionCount();
}

/// <summary>
/// Coordinator grain for testing distributed transactions
/// </summary>
public interface ITransferCoordinatorGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Execute a transfer between two accounts (distributed transaction)
    /// </summary>
    [Transaction(TransactionOption.Create)]
    Task Transfer(string fromAccountId, string toAccountId, decimal amount);
}
