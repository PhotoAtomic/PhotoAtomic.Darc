using PhotoAtomic.DeepCloner;
using PhotoAtomic.Darc.Test.TestGrains;
using Xunit;

namespace PhotoAtomic.Darc.Test;

public class DeepClonerTests
{
    [Fact]
    public void Clone_SimpleState_CreatesIndependentCopy()
    {
        // Arrange
        var original = new TestGrains.BankAccountState
        {
            Balance = 1000m,
            TransactionCount = 5,
            LastUpdate = DateTime.UtcNow
        };

        // Act
        var clone = original.Clone();

        // Assert
        Assert.NotNull(clone);
        Assert.NotSame(original, clone);
        Assert.Equal(original.Balance, clone.Balance);
        Assert.Equal(original.TransactionCount, clone.TransactionCount);
        Assert.Equal(original.LastUpdate, clone.LastUpdate);
        
        // Verify independence
        clone.Balance = 2000m;
        Assert.Equal(1000m, original.Balance);
    }

    [Fact]
    public void Clone_WithPendingEvents_SkipsPendingEventsList()
    {
        // Arrange
        var original = new TestGrains.BankAccountState
        {
            Balance = 1000m,
            TransactionCount = 5,
            LastUpdate = DateTime.UtcNow
        };
        
        // Add pending events
        original.PendingEventsList.Add(new TestGrains.MoneyDepositedEvent(100m));
        original.PendingEventsList.Add(new TestGrains.MoneyWithdrawnEvent(50m));

        // Act
        var clone = original.Clone();

        // Assert - PendingEventsList should be empty (skipped by [SkipClone])
        Assert.NotNull(clone);
        Assert.Empty(clone.PendingEventsList);
        Assert.Equal(2, original.PendingEventsList.Count);
    }

    [Fact]
    public void Clone_WithCloneContext_TracksReferences()
    {
        // Arrange
        var original = new TestGrains.BankAccountState
        {
            Balance = 1000m,
            TransactionCount = 5,
            LastUpdate = DateTime.UtcNow
        };
        
        var context = new CloneContext();

        // Act
        var clone1 = original.Clone(context);
        var clone2 = original.Clone(context);

        // Assert - same source should return same clone instance
        Assert.Same(clone1, clone2);
        Assert.Equal(1, context.Count);
    }
}
