using PhotoAtomic.Clooney;
using System.Linq;
using Xunit;

namespace PhotoAtomic.Darc.Test;

/// <summary>
/// Tests to demonstrate that partial classes marked with [Clonable], [Hashable], and [Diffable]
/// implement the corresponding interfaces and can be used directly through interface casting.
/// </summary>
public class InterfaceImplementationTests
{
    #region IClonable<T> Interface Tests

    [Fact]
    public void PartialClass_WithClonable_ImplementsIClonable()
    {
        // Arrange
        var original = new ClonableTestModel
        {
            Id = 42,
            Name = "Test",
            Value = 3.14
        };

        // Act - Cast to interface and use it
        IClonable<ClonableTestModel> clonable = (IClonable<ClonableTestModel>)original;
        var cloned = clonable.Clone();

        // Assert
        Assert.NotNull(cloned);
        Assert.NotSame(original, cloned);
        Assert.Equal(original.Id, cloned!.Id);
        Assert.Equal(original.Name, cloned.Name);
        Assert.Equal(original.Value, cloned.Value);
    }

    [Fact]
    public void PartialClass_WithClonable_CanCallCloneDirectly()
    {
        // Arrange
        var original = new ClonableTestModel
        {
            Id = 100,
            Name = "Direct Call",
            Value = 2.71
        };

        // Act - Call Clone() directly as instance method
        var cloned = original.Clone();

        // Assert
        Assert.NotNull(cloned);
        Assert.Equal(original.Id, cloned!.Id);
        Assert.Equal(original.Name, cloned.Name);
    }

    [Fact]
    public void PartialClass_WithClonable_SupportsPolymorphicUsage()
    {
        // Arrange - Create a list of IClonable instances
        var items = new IClonable<ClonableTestModel>[]
        {
            (IClonable<ClonableTestModel>)new ClonableTestModel { Id = 1, Name = "First", Value = 1.0 },
            (IClonable<ClonableTestModel>)new ClonableTestModel { Id = 2, Name = "Second", Value = 2.0 },
            (IClonable<ClonableTestModel>)new ClonableTestModel { Id = 3, Name = "Third", Value = 3.0 }
        };

        // Act - Clone all items using the interface
        var clones = items.Select(x => x.Clone()).ToList();

        // Assert
        for (int i = 0; i < items.Length; i++)
        {
            Assert.NotNull(clones[i]);
            Assert.Equal(((ClonableTestModel)items[i]).Id, clones[i]!.Id);
            Assert.Equal(((ClonableTestModel)items[i]).Name, clones[i]!.Name);
        }
    }

    #endregion

    #region IHashable Interface Tests

    [Fact]
    public void PartialClass_WithHashable_ImplementsIHashable()
    {
        // Arrange
        var obj = new HashableTestModel
        {
            Key = "test-key",
            Counter = 42,
            Flag = true
        };

        // Act - Cast to interface and use it
        IHashable hashable = (IHashable)obj;
        var hash = hashable.HashValue();

        // Assert
        Assert.NotEqual(0, hash);
    }

    [Fact]
    public void PartialClass_WithHashable_ProducesSameHashForEqualValues()
    {
        // Arrange
        var obj1 = new HashableTestModel
        {
            Key = "same-key",
            Counter = 123,
            Flag = false
        };

        var obj2 = new HashableTestModel
        {
            Key = "same-key",
            Counter = 123,
            Flag = false
        };

        // Act - Call HashValue() directly
        var hash1 = obj1.HashValue();
        var hash2 = obj2.HashValue();

        // Assert
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void PartialClass_WithHashable_SupportsCollectionOfHashables()
    {
        // Arrange - Create a collection of IHashable instances
        var hashables = new IHashable[]
        {
            (IHashable)new HashableTestModel { Key = "a", Counter = 1, Flag = true },
            (IHashable)new HashableTestModel { Key = "b", Counter = 2, Flag = false },
            (IHashable)new HashableTestModel { Key = "c", Counter = 3, Flag = true }
        };

        // Act - Compute hashes using the interface
        var hashes = hashables.Select(h => h.HashValue()).ToArray();

        // Assert - All hashes should be different (highly likely)
        Assert.NotEqual(hashes[0], hashes[1]);
        Assert.NotEqual(hashes[1], hashes[2]);
        Assert.NotEqual(hashes[0], hashes[2]);
    }

    #endregion

    #region IDifferentiable<T> Interface Tests

    [Fact]
    public void PartialClass_WithDiffable_ImplementsIDifferentiable()
    {
        // Arrange
        var obj1 = new DiffableTestModel
        {
            Name = "Original",
            Version = 1,
            Active = true
        };

        var obj2 = new DiffableTestModel
        {
            Name = "Modified",
            Version = 1,
            Active = true
        };

        // Act - Cast to interface and use it
        IDifferentiable<DiffableTestModel> diffable = (IDifferentiable<DiffableTestModel>)obj1;
        var differences = diffable.Diff(obj2);

        // Assert
        Assert.NotNull(differences);
        var diffList = differences.ToList();
        Assert.NotEmpty(diffList); // Should have at least one difference (Name)
    }

    [Fact]
    public void PartialClass_WithDiffable_DetectsNoDifferencesForIdenticalObjects()
    {
        // Arrange
        var obj1 = new DiffableTestModel
        {
            Name = "Same",
            Version = 5,
            Active = false
        };

        var obj2 = new DiffableTestModel
        {
            Name = "Same",
            Version = 5,
            Active = false
        };

        // Act - Call Diff() directly
        var differences = obj1.Diff(obj2);

        // Assert
        Assert.Empty(differences);
    }

    [Fact]
    public void PartialClass_WithDiffable_SupportsPolymorphicComparison()
    {
        // Arrange - Create a list of differentiable objects
        var baseline = new DiffableTestModel
        {
            Name = "Baseline",
            Version = 1,
            Active = true
        };

        var comparisons = new IDifferentiable<DiffableTestModel>[]
        {
            (IDifferentiable<DiffableTestModel>)new DiffableTestModel { Name = "Baseline", Version = 1, Active = true },
            (IDifferentiable<DiffableTestModel>)new DiffableTestModel { Name = "Changed", Version = 1, Active = true },
            (IDifferentiable<DiffableTestModel>)new DiffableTestModel { Name = "Baseline", Version = 2, Active = true }
        };

        // Act - Compare using the interface
        var results = comparisons.Select(c => !c.Diff(baseline).Any()).ToArray();

        // Assert
        Assert.True(results[0]);   // Same as baseline
        Assert.False(results[1]);  // Different name
        Assert.False(results[2]);  // Different version
    }

    [Fact]
    public void PartialClass_WithDiffable_CanCallDiffDirectly()
    {
        // Arrange
        var obj1 = new DiffableTestModel
        {
            Name = "Test",
            Version = 1,
            Active = true
        };

        var obj2 = new DiffableTestModel
        {
            Name = "Test",
            Version = 2,
            Active = true
        };

        // Act - Call Diff() directly as instance method
        var differences = obj1.Diff(obj2);

        // Assert
        Assert.NotNull(differences);
        var diffList = differences.ToList();
        Assert.NotEmpty(diffList); // Should detect Version difference
        Assert.Contains(diffList, d => d.ToString().Contains("Version"));
    }

    #endregion
}



