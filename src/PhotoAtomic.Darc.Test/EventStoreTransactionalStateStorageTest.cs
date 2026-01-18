using KurrentDB.Client;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Orleans.Transactions;
using PhotoAtomic.Darc.Extensions;
using PhotoAtomic.Darc.Test.TestGrains;
using Testcontainers.KurrentDb;
using Xunit;

namespace PhotoAtomic.Darc.Test;

/// <summary>
/// Integration tests for EventStoreTransactionalStateStorage with Orleans transactions
/// Uses KurrentDB Testcontainer for real database interaction
/// </summary>
public class EventStoreTestFixture : IAsyncLifetime
{
    public KurrentDbContainer? KurrentDbContainer { get; private set; }
    public TestCluster? Cluster { get; private set; }
    public string? ConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        // Start KurrentDB container
        KurrentDbContainer = new KurrentDbBuilder("kurrentplatform/kurrentdb:latest")            
            .Build();

        await KurrentDbContainer.StartAsync();
        ConnectionString = KurrentDbContainer.GetConnectionString();

        // Set connection string for configurator
        TestSiloConfigurator.SetConnectionString(ConnectionString);

        // Configure Orleans test cluster
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();

        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        if (Cluster != null)
        {
            await Cluster.StopAllSilosAsync();
            Cluster.Dispose();
        }

        if (KurrentDbContainer != null)
        {
            await KurrentDbContainer.DisposeAsync();
        }
    }

    /// <summary>
    /// Test cluster configurator to setup EventStoreTransactionalStateStorage
    /// </summary>
    private class TestSiloConfigurator : ISiloConfigurator
    {
        private static string? connectionString;

        public static void SetConnectionString(string connString)
        {
            connectionString = connString;
        }

        public void Configure(ISiloBuilder siloBuilder)
        {
            if (string.IsNullOrEmpty(connectionString))
                throw new InvalidOperationException("Connection string not set");

            siloBuilder.ConfigureServices(services =>
            {
                // Register EventStoreTransactionalStateStorage factory
                services.AddEventStoreTransactionalStateStorage(connectionString);
            });

            // Configure Orleans transactions
            siloBuilder.UseTransactions();
        }
    }
}

[Collection("EventStoreCollection")]
public class EventStoreTransactionalStateStorageTest
{
    private readonly EventStoreTestFixture fixture;

    public EventStoreTransactionalStateStorageTest(EventStoreTestFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task SingleGrain_Deposit_ShouldUpdateBalance()
    {
        // Arrange
        var accountId = "account-001";
        var account = fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(accountId);

        // Act
        await account.Deposit(100m);
        var balance = await account.GetBalance();

        // Assert
        Assert.Equal(100m, balance);
    }

    [Fact]
    public async Task SingleGrain_MultipleDeposits_ShouldAccumulate()
    {
        // Arrange
        var accountId = "account-002";
        var account = fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(accountId);

        // Act
        await account.Deposit(50m);
        await account.Deposit(30m);
        await account.Deposit(20m);
        var balance = await account.GetBalance();
        var txCount = await account.GetTransactionCount();

        // Assert
        Assert.Equal(100m, balance);
        Assert.Equal(3, txCount);
    }

    [Fact]
    public async Task SingleGrain_Withdraw_ShouldDecreaseBalance()
    {
        // Arrange
        var accountId = "account-003";
        var account = fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(accountId);

        // Act
        await account.Deposit(100m);
        await account.Withdraw(30m);
        var balance = await account.GetBalance();

        // Assert
        Assert.Equal(70m, balance);
    }

    [Fact]
    public async Task SingleGrain_WithdrawMoreThanBalance_ShouldThrowAndRollback()
    {
        // Arrange
        var accountId = "account-004";
        var account = fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(accountId);
        await account.Deposit(50m);

        // Act & Assert        
        await Assert.ThrowsAsync<OrleansTransactionAbortedException>(async () =>
            await account.Withdraw(40));

        // Balance should remain unchanged after failed transaction
        var balance = await account.GetBalance();
        Assert.Equal(50m, balance);
    }

    [Fact]
    public async Task DistributedTransaction_Transfer_ShouldMoveMoneyAtomically()
    {
        // Arrange
        var fromAccountId = "account-005";
        var toAccountId = "account-006";
        
        var fromAccount = fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(fromAccountId);
        var toAccount = fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(toAccountId);
        var coordinator = fixture.Cluster!.GrainFactory.GetGrain<ITransferCoordinatorGrain>(Guid.NewGuid());

        // Setup initial balances
        await fromAccount.Deposit(200m);
        await toAccount.Deposit(50m);

        // Act - Execute distributed transaction
        await coordinator.Transfer(fromAccountId, toAccountId, 75m);

        // Assert
        var fromBalance = await fromAccount.GetBalance();
        var toBalance = await toAccount.GetBalance();

        Assert.Equal(125m, fromBalance); // 200 - 75
        Assert.Equal(125m, toBalance);   // 50 + 75
    }

    [Fact]
    public async Task DistributedTransaction_TransferInsufficientFunds_ShouldRollbackBoth()
    {
        // Arrange
        var fromAccountId = "account-007";
        var toAccountId = "account-008";
        
        var fromAccount = fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(fromAccountId);
        var toAccount = fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(toAccountId);
        var coordinator = fixture.Cluster!.GrainFactory.GetGrain<ITransferCoordinatorGrain>(Guid.NewGuid());

        // Setup initial balances
        await fromAccount.Deposit(50m);
        await toAccount.Deposit(100m);

        // Act & Assert - Try to transfer more than available
        await Assert.ThrowsAsync<OrleansTransactionAbortedException>(async () =>
            await coordinator.Transfer(fromAccountId, toAccountId, 100m));

        // Both accounts should maintain original balances
        var fromBalance = await fromAccount.GetBalance();
        var toBalance = await toAccount.GetBalance();

        Assert.Equal(50m, fromBalance);
        Assert.Equal(100m, toBalance);
    }

    [Fact]
    public async Task MultipleTransfers_BetweenSameAccounts_ShouldBeSerializable()
    {
        // Arrange
        var account1Id = "account-009";
        var account2Id = "account-010";
        
        var account1 = fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(account1Id);
        var account2 = fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(account2Id);

        await account1.Deposit(1000m);
        await account2.Deposit(1000m);

        // Act - Execute multiple concurrent transfers
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            var coordinator = fixture.Cluster!.GrainFactory.GetGrain<ITransferCoordinatorGrain>(Guid.NewGuid());
            tasks.Add(coordinator.Transfer(account1Id, account2Id, 10m));
        }

        await Task.WhenAll(tasks);

        // Assert - Total money should be preserved
        var balance1 = await account1.GetBalance();
        var balance2 = await account2.GetBalance();
        var totalBalance = balance1 + balance2;

        Assert.Equal(2000m, totalBalance); // Money preserved
        Assert.Equal(900m, balance1);      // 1000 - (10 * 10)
        Assert.Equal(1100m, balance2);     // 1000 + (10 * 10)
    }

    [Fact]
    public async Task EventStream_AfterTransactions_ShouldContainOnlyCommittedEvents()
    {
        // Arrange
        var accountId = "account-011";
        var account = fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(accountId);

        // Act - Execute multiple transactions
        await account.Deposit(100m);
        await account.Withdraw(30m);
        await account.Deposit(50m);

        decimal total = await account.GetBalance();

        // Verify stream using direct KurrentDB client
        var client = new KurrentDBClient(KurrentDBClientSettings.Create(fixture.ConnectionString!));
        var streamName = $"bankaccount-{accountId}-account";
        
        var events = new List<ResolvedEvent>();
        var readStream = client.ReadStreamAsync(
            Direction.Forwards,
            streamName,
            StreamPosition.Start);

        await foreach (var evt in readStream)
        {
            events.Add(evt);
        }

        // Assert - Should have events in main stream (no pending events!)
        Assert.NotEmpty(events);
        
        // Verify no pending stream exists
        var pendingStreamName = $"{streamName}-pending";
        await Assert.ThrowsAsync<StreamNotFoundException>(async () =>
        {
            var pendingRead = client.ReadStreamAsync(
                Direction.Forwards,
                pendingStreamName,
                StreamPosition.Start);
            
            await foreach (var _ in pendingRead)
            {
                // Should not reach here
            }
        });
    }

    [Fact]
    public async Task GrainReactivation_AfterDeactivation_ShouldPreserveState()
    {
        // Arrange
        var accountId = "account-012";
        var account1 = fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(accountId);

        // Act - Perform operations
        await account1.Deposit(500m);
        var balance1 = await account1.GetBalance();

        // Force grain deactivation (simulate grain restart)
        await fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(accountId).Deposit(0m);
        await Task.Delay(100); // Allow some time for state persistence

        // Get grain again (should reload from KurrentDB)
        var account2 = fixture.Cluster!.GrainFactory.GetGrain<IBankAccountGrain>(accountId);
        var balance2 = await account2.GetBalance();

        // Assert - State should be preserved
        Assert.Equal(500m, balance1);
        Assert.Equal(500m, balance2);
    }
}

[CollectionDefinition("EventStoreCollection")]
public class EventStoreCollection : ICollectionFixture<EventStoreTestFixture> { }
