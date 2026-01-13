using Orleans;

namespace PhotoAtomic.Darc.Test.TestGrains;

/// <summary>
/// Event raised when money is deposited into an account
/// </summary>
[GenerateSerializer]
public record MoneyDepositedEvent([property: Id(1)] decimal Amount) : Event;

/// <summary>
/// Event raised when money is withdrawn from an account
/// </summary>
[GenerateSerializer]
public record MoneyWithdrawnEvent([property: Id(1)] decimal Amount) : Event;

/// <summary>
/// Event raised when an account is created
/// </summary>
[GenerateSerializer]
public record AccountCreatedEvent([property: Id(1)] string AccountId) : Event;
